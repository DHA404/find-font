using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FindFont.App.Services;

/// <summary>渲染请求参数。</summary>
public sealed record RenderRequest(string FontKey, string? FontFilePath, string Text, double FontSize, int PixelWidth, int PixelHeight, double Dpi = 96);

/// <summary>
/// 离屏位图渲染与缓存引擎（计划书核心方案）。
/// - 使用 <see cref="Typeface"/> + <see cref="FormattedText"/> + <see cref="RenderTargetBitmap"/> 出图，
///   完全绕开 UI 层的 <see cref="FontFamily"/> 绑定；
/// - 位图 Freeze() 后跨线程共享，UI 只放 Image；
/// - 内存级 LRU 位图缓存，超出容量自动淘汰；
/// - 渲染跑在独立 STA 线程的 Dispatcher 上，UI 线程永不阻塞。
/// </summary>
public sealed class FontRenderCache : IDisposable
{
    private readonly record struct CacheKey(string FontKey, string Text, double FontSize, int Width, int Height, double Dpi);

    private readonly int _capacity;
    private readonly Dictionary<CacheKey, LinkedListNode<(CacheKey Key, BitmapSource Bitmap)>> _map = new();
    private readonly LinkedList<(CacheKey Key, BitmapSource Bitmap)> _lru = new();
    private readonly object _sync = new();

    private readonly Dictionary<string, GlyphTypeface> _glyphs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _invalidGlyphs = new(StringComparer.OrdinalIgnoreCase);

    private readonly Thread _renderThread;
    private readonly Dispatcher _renderDispatcher;
    private readonly ManualResetEventSlim _ready = new(false);

    private bool _disposed;

    public FontRenderCache(int capacity = 600)
    {
        _capacity = Math.Max(32, capacity);

        Dispatcher? readyDispatcher = null;
        var thread = new Thread(() =>
        {
            readyDispatcher = Dispatcher.CurrentDispatcher;
            _ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "FindFont.Render",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5)) || readyDispatcher is null)
            throw new InvalidOperationException("渲染线程启动失败。");

        _renderThread = thread;
        _renderDispatcher = readyDispatcher;
    }

    /// <summary>当前缓存位图数量。</summary>
    public int Count
    {
        get { lock (_sync) return _map.Count; }
    }

    /// <summary>同步取缓存；没有则返回 null。</summary>
    public BitmapSource? TryGetFromCache(RenderRequest request)
    {
        var key = ToKey(request);
        lock (_sync)
        {
            if (!_map.TryGetValue(key, out var node)) return null;
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Bitmap;
        }
    }

    /// <summary>
    /// 渲染（或命中缓存）生成位图。返回的 BitmapSource 已 Freeze，可安全跨线程使用。
    /// 渲染失败返回占位符位图，绝不抛出。
    /// </summary>
    public Task<BitmapSource> RenderAsync(RenderRequest request, CancellationToken token = default)
    {
        var cached = TryGetFromCache(request);
        if (cached is not null) return Task.FromResult(cached);

        var tcs = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_disposed)
        {
            tcs.TrySetResult(Placeholder(request));
            return tcs.Task;
        }

        token.Register(() => tcs.TrySetCanceled(token));

        _renderDispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (token.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(token);
                    return;
                }

                var key = ToKey(request);
                lock (_sync)
                {
                    if (_map.TryGetValue(key, out var node))
                    {
                        _lru.Remove(node);
                        _lru.AddFirst(node);
                        tcs.TrySetResult(node.Value.Bitmap);
                        return;
                    }
                }

                BitmapSource bitmap;
                try
                {
                    bitmap = RenderCore(request);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FileFormatException
                                               or System.Runtime.InteropServices.COMException or NotSupportedException
                                               or IOException)
                {
                    bitmap = Placeholder(request);
                }

                bitmap.Freeze();
                Store(key, bitmap);
                tcs.TrySetResult(bitmap);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }), DispatcherPriority.Background);

        return tcs.Task;
    }

    private BitmapSource RenderCore(RenderRequest request)
    {
        var glyph = ResolveGlyphTypeface(request.FontFilePath);
        var typeface = glyph is not null && glyph.FamilyNames.Count > 0
            ? new Typeface(new FontFamily(glyph.FamilyNames.Values.First()), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal)
            : new Typeface("Segoe UI");

        var dpi = request.Dpi <= 0 ? 96 : request.Dpi;
        var scale = dpi / 96.0;

        var formatted = new FormattedText(
            string.IsNullOrEmpty(request.Text) ? " " : request.Text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            request.FontSize,
            Brushes.Black,
            null,
            TextFormattingMode.Ideal,
            scale)
        {
            MaxTextWidth = Math.Max(8, request.PixelWidth - 8),
            MaxTextHeight = Math.Max(8, request.PixelHeight),
            Trimming = TextTrimming.CharacterEllipsis,
            LineHeight = Math.Max(request.FontSize * 1.15, request.FontSize),
        };

        // 垂直居中：展开态有 150px 高，贴着顶画会让字形浮在上半边。
        var top = Math.Max(0, (request.PixelHeight - formatted.Height) / 2);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, request.PixelWidth, request.PixelHeight));
            dc.DrawText(formatted, new Point(2, top));
        }

        var rtb = new RenderTargetBitmap(
            Math.Max(1, request.PixelWidth),
            Math.Max(1, request.PixelHeight),
            dpi,
            dpi,
            PixelFormats.Pbgra32);
        rtb.Render(visual);
        return rtb;
    }

    private GlyphTypeface? ResolveGlyphTypeface(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        lock (_sync)
        {
            if (_glyphs.TryGetValue(path, out var cached)) return cached;
            if (_invalidGlyphs.ContainsKey(path)) return null;
        }

        GlyphTypeface? glyph = null;
        try
        {
            if (File.Exists(path)) glyph = new GlyphTypeface(new Uri(Path.GetFullPath(path)));
        }
        catch (Exception ex) when (ex is FileFormatException or NotSupportedException or ArgumentException or IOException)
        {
            glyph = null;
        }

        lock (_sync)
        {
            if (glyph is null)
            {
                _invalidGlyphs[path] = true;
                return null;
            }

            _glyphs[path] = glyph;
            return glyph;
        }
    }

    private static CacheKey ToKey(RenderRequest r) =>
        new(r.FontKey, r.Text, r.FontSize, r.PixelWidth, r.PixelHeight, r.Dpi);

    private void Store(CacheKey key, BitmapSource bitmap)
    {
        lock (_sync)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _map.Remove(key);
            }

            var node = _lru.AddFirst((key, bitmap));
            _map[key] = node;

            while (_map.Count > _capacity && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    private static BitmapSource Placeholder(RenderRequest request)
    {
        var width = Math.Max(1, request.PixelWidth);
        var height = Math.Max(1, request.PixelHeight);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
            var text = new FormattedText(
                "Unknown Font",
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                Math.Min(18, request.FontSize),
                Brushes.Gray,
                null,
                TextFormattingMode.Ideal,
                1.0);
            dc.DrawText(text, new Point(2, Math.Max(0, height / 2.0 - text.Height / 2)));
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    public void Clear()
    {
        lock (_sync)
        {
            _map.Clear();
            _lru.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _renderDispatcher.InvokeShutdown();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException)
        {
            // 渲染线程已退出。
        }

        lock (_sync)
        {
            _map.Clear();
            _lru.Clear();
            _glyphs.Clear();
            _invalidGlyphs.Clear();
        }
    }
}
