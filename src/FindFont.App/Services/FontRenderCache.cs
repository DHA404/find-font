using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FindFont.App.Services;

/// <summary>渲染请求参数。</summary>
/// <param name="FamilyName">
/// 家族名提示。<c>.ttc/.otc</c> 一个文件装着多个字体，只靠路径无法定位到正确的字面，
/// 必须用「文件 URI + #家族名」才能取到对应成员；为空时退回按路径取首个成员。
/// </param>
public sealed record RenderRequest(
    string FontKey,
    string? FontFilePath,
    string Text,
    double FontSize,
    int PixelWidth,
    int PixelHeight,
    double Dpi = 96,
    string? FamilyName = null);

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
public sealed partial class FontRenderCache : IDisposable
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

    // 同一键的并发请求合并：只投递一次渲染，其余 await 同一个 Task。
    private readonly Dictionary<CacheKey, TaskCompletionSource<BitmapSource>> _inflight = new();

    // 陈旧的同一字体请求丢弃：记录每个字体当前最新的请求序号。

    private readonly Dictionary<(int Width, int Height), BitmapSource> _placeholders = new();

    // 渲染线程池：每个线程自带 Dispatcher 与独占 Typeface 缓存（见 FontRenderCache.Pool.cs）。
    private readonly RenderWorker[] _workers;

    private long _renderedFrames;
    private bool _disposed;

    public FontRenderCache(int capacity = 800, long byteBudgetBytes = DefaultByteBudget)
    {
        _capacity = Math.Max(32, capacity);
        _byteBudget = Math.Max(4L * 1024 * 1024, byteBudgetBytes);

        _workers = StartWorkers();
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

    /// <summary>当前渲染线程数量（半核起步，夹在 2–4）。</summary>
    public int WorkerThreads => _workers.Length;

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
        lock (_sync)
        {
            // 同键在途：直接复用，避免同一张图被重复渲染。
            if (_inflight.TryGetValue(key, out var running))
            {
                return running.Task;
            }

            tcs = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            _inflight[key] = tcs;
        }

        if (token.CanBeCanceled)
        {
            token.Register(() =>
            {
                lock (_sync) _inflight.Remove(key);
                tcs.TrySetCanceled(token);
            });
        }

        var worker = PickWorker(key);
        var dispatcher = worker.Dispatcher;
        if (dispatcher is null)
        {
            lock (_sync) _inflight.Remove(key);
            return Task.FromResult(Placeholder(request));
        }

        dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                // 这里曾经有「同字体有更新的请求就把旧请求取消掉」的优化。它省下的渲染
                // 时间是真实的，但代价是：旧请求的 await 方拿到取消异常后什么都不做，
                // 于是一旦新旧两批请求交错（改列宽、改预览字号、改 DPI 都会造成这种交错），
                // 被取消的那张图就永远不会落到 PreviewBitmap 上，界面停在「正在渲染…」。
                // 因此这里只尊重调用方显式传入的取消令牌，不再按序号丢弃请求；
                // 重复渲染由位图缓存与 _inflight 去重兜底。
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
                    bitmap = RenderCore(request, worker);
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

        StopWorkers();

        lock (_sync)
        {
            _map.Clear();
            _lru.Clear();
            _bytes = 0;
            foreach (var worker in _workers)
            {
                worker.ClearCaches();
            }

            _placeholders.Clear();
        }
    }
}
