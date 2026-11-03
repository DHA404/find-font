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
/// - 内存级 LRU 位图缓存，同时受条数与字节预算约束（滚动时不会把内存吃穿）；
/// - 同一键的并发请求合并成一次真实渲染；
/// - 同一字体已有更新的请求排队时，旧请求直接丢弃，滚动不会排队积压；
/// - 渲染跑在独立 STA 线程的 Dispatcher 上，UI 线程永不阻塞。
/// </summary>
public sealed class FontRenderCache : IDisposable
{
    /// <summary>默认字节预算：96 MB（约等于 400 张 1500×220 位图）。</summary>
    public const long DefaultByteBudget = 96L * 1024 * 1024;

    private readonly record struct CacheKey(string FontKey, string Text, double FontSize, int Width, int Height, double Dpi)
    {
        public long Bytes => (long)Math.Max(1, Width) * Math.Max(1, Height) * 4;
    }

    private readonly int _capacity;
    private readonly long _byteBudget;
    private readonly Dictionary<CacheKey, LinkedListNode<(CacheKey Key, BitmapSource Bitmap)>> _map = new();
    private readonly LinkedList<(CacheKey Key, BitmapSource Bitmap)> _lru = new();
    private readonly object _sync = new();
    private long _bytes;

    private readonly Dictionary<string, GlyphTypeface> _glyphs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Typeface?> _typefaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _invalidGlyphs = new(StringComparer.OrdinalIgnoreCase);

    // 同一键的并发请求合并：只投递一次渲染，其余 await 同一个 Task。
    private readonly Dictionary<CacheKey, TaskCompletionSource<BitmapSource>> _inflight = new();

    // 陈旧的同一字体请求丢弃：记录每个字体当前最新的请求序号。
    private readonly Dictionary<string, long> _latestSeqByFont = new(StringComparer.OrdinalIgnoreCase);
    private long _seq;

    private readonly Dictionary<(int Width, int Height), BitmapSource> _placeholders = new();

    private readonly Thread _renderThread;
    private readonly Dispatcher _renderDispatcher;
    private readonly ManualResetEventSlim _ready = new(false);

    private long _renderedFrames;
    private bool _disposed;

    public FontRenderCache(int capacity = 800, long byteBudgetBytes = DefaultByteBudget)
    {
        _capacity = Math.Max(32, capacity);
        _byteBudget = Math.Max(4L * 1024 * 1024, byteBudgetBytes);

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

    /// <summary>当前缓存位图占用的近似字节数。</summary>
    public long Bytes
    {
        get { lock (_sync) return _bytes; }
    }

    /// <summary>累计真正执行的渲染次数（缓存命中与合并都不计数），供测试与诊断。</summary>
    public long RenderedFrames => Interlocked.Read(ref _renderedFrames);

    /// <summary>字节预算上限。</summary>
    public long ByteBudget => _byteBudget;

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
        if (_disposed) return Task.FromResult(Placeholder(request));

        var cached = TryGetFromCache(request);
        if (cached is not null) return Task.FromResult(cached);

        var key = ToKey(request);

        TaskCompletionSource<BitmapSource> tcs;
        long seq;
        lock (_sync)
        {
            // 同键在途：直接复用，避免同一张图被重复渲染。
            if (_inflight.TryGetValue(key, out var running))
            {
                return running.Task;
            }

            tcs = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            _inflight[key] = tcs;
            seq = ++_seq;
            _latestSeqByFont[request.FontKey] = seq;
        }

        if (token.CanBeCanceled)
        {
            token.Register(() =>
            {
                lock (_sync) _inflight.Remove(key);
                tcs.TrySetCanceled(token);
            });
        }

        _renderDispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                // 同字体已有更新的请求在排队：这张图滚出视野了，别浪费渲染时间。
                lock (_sync)
                {
                    if (_latestSeqByFont.TryGetValue(request.FontKey, out var latest) && latest != seq)
                    {
                        _inflight.Remove(key);
                        tcs.TrySetCanceled();
                        return;
                    }
                }

                if (token.IsCancellationRequested)
                {
                    lock (_sync) _inflight.Remove(key);
                    tcs.TrySetCanceled(token);
                    return;
                }

                lock (_sync)
                {
                    if (_map.TryGetValue(key, out var node))
                    {
                        _lru.Remove(node);
                        _lru.AddFirst(node);
                        _inflight.Remove(key);
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
                Interlocked.Increment(ref _renderedFrames);
                Store(key, bitmap);

                lock (_sync) _inflight.Remove(key);
                tcs.TrySetResult(bitmap);
            }
            catch (Exception ex)
            {
                lock (_sync) _inflight.Remove(key);
                tcs.TrySetException(ex);
            }
        }), DispatcherPriority.Background);

        return tcs.Task;
    }

    private BitmapSource RenderCore(RenderRequest request)
    {
        var typeface = ResolveTypeface(request.FontFilePath);

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

        // 垂直居中：展开态有 220px 高，贴着顶画会让字形浮在上半边。
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

    /// <summary>
    /// 取 Typeface。首次为某文件构造时优先用「文件 URI + #家族名」，
    /// 避开按家族名二次查找系统字体族的开销，也避免同类名撞车。
    /// </summary>
    private Typeface ResolveTypeface(string? path)
    {
        var fallback = new Typeface("Segoe UI");
        if (string.IsNullOrWhiteSpace(path)) return fallback;

        lock (_sync)
        {
            if (_typefaces.TryGetValue(path, out var cached)) return cached ?? fallback;
        }

        var glyph = ResolveGlyphTypeface(path);
        Typeface? built = null;

        if (glyph is not null && glyph.FamilyNames.Count > 0)
        {
            var family = glyph.FamilyNames.Values.First();
            try
            {
                built = new Typeface(
                    new FontFamily(new Uri(Path.GetFullPath(path)), "./#" + family),
                    FontStyles.Normal,
                    FontWeights.Normal,
                    FontStretches.Normal);
                if (!built.TryGetGlyphTypeface(out _)) built = null;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
            {
                built = null;
            }

            built ??= new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        }

        lock (_sync)
        {
            _typefaces[path] = built;
            return built ?? fallback;
        }
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
                _bytes -= existing.Value.Key.Bytes;
            }

            var node = _lru.AddFirst((key, bitmap));
            _map[key] = node;
            _bytes += key.Bytes;

            // 双约束：条数上限 + 字节预算。永远保留刚插入的那一张。
            while ((_map.Count > _capacity || _bytes > _byteBudget) && _lru.Last is { } last && _map.Count > 1)
            {
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
                _bytes -= last.Value.Key.Bytes;
            }

            if (_bytes < 0) _bytes = 0;
        }
    }

    private BitmapSource Placeholder(RenderRequest request)
    {
        var width = Math.Max(1, request.PixelWidth);
        var height = Math.Max(1, request.PixelHeight);
        var size = Math.Min(18, request.FontSize);

        lock (_sync)
        {
            if (_placeholders.TryGetValue((width, height), out var cached) && Math.Abs(size - 18) < 0.01)
                return cached;
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
            var text = new FormattedText(
                "Unknown Font",
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                size,
                Brushes.Gray,
                null,
                TextFormattingMode.Ideal,
                1.0);
            dc.DrawText(text, new Point(2, Math.Max(0, height / 2.0 - text.Height / 2)));
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();

        if (Math.Abs(size - 18) < 0.01)
        {
            lock (_sync) _placeholders[(width, height)] = rtb;
        }

        return rtb;
    }

    public void Clear()
    {
        lock (_sync)
        {
            _map.Clear();
            _lru.Clear();
            _bytes = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        TaskCompletionSource<BitmapSource>[] pending;
        lock (_sync)
        {
            pending = _inflight.Values.ToArray();
            _inflight.Clear();
        }

        foreach (var tcs in pending) tcs.TrySetCanceled();

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
            _bytes = 0;
            _glyphs.Clear();
            _typefaces.Clear();
            _invalidGlyphs.Clear();
            _placeholders.Clear();
        }
    }
}
