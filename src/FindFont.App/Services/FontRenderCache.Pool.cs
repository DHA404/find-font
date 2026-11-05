using System.Windows.Media;
using System.Windows.Threading;

namespace FindFont.App.Services;

/// <summary>
/// 渲染线程池。拆出此文件只为守住单文件 300 行的规约。
/// <para>
/// <see cref="RenderTargetBitmap"/> 要求 STA，所以每个工作线程自带一个 Dispatcher；
/// 请求按缓存键哈希固定投递到某一条线程，既能让多核并行出图，又能保证同一个键
/// 永远落在同一条线程上（Typeface / GlyphTypeface 不是线程安全的，不能跨线程共享）。
/// </para>
/// </summary>
public sealed partial class FontRenderCache
{
    /// <summary>一条渲染线程：自带 Dispatcher 与只属于它的 Typeface 缓存。</summary>
    private sealed class RenderWorker
    {
        public RenderWorker(int index)
        {
            Index = index;
        }

        public int Index { get; }

        public Thread? Thread { get; set; }

        public Dispatcher? Dispatcher { get; set; }

        public ManualResetEventSlim Ready { get; } = new(false);

        /// <summary>本线程独占的 Typeface 缓存（路径或「路径#家族名」→ Typeface）。</summary>
        public Dictionary<string, Typeface?> Typefaces { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>本线程独占的 GlyphTypeface 缓存（文件路径 → 字形）。</summary>
        public Dictionary<string, GlyphTypeface> Glyphs { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>本线程独占的「读不出字形」标记，避免反复打开坏文件。</summary>
        public Dictionary<string, bool> InvalidGlyphs { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>清空本线程全部缓存。</summary>
        public void ClearCaches()
        {
            Typefaces.Clear();
            Glyphs.Clear();
            InvalidGlyphs.Clear();
        }
    }

    /// <summary>渲染线程数量：半核起步，夹在 2–4 之间（再多收益递减且吃内存）。</summary>
    private static int WorkerCount => Math.Clamp(Environment.ProcessorCount / 2, 2, 4);

    /// <summary>按缓存键哈希选线程；同一个字体永远落在同一条线程。</summary>
    private RenderWorker PickWorker(CacheKey key)
    {
        var index = (key.GetHashCode() & 0x7FFFFFFF) % _workers.Length;
        return _workers[index];
    }

    private RenderWorker[] StartWorkers()
    {
        var workers = new RenderWorker[WorkerCount];
        for (var i = 0; i < workers.Length; i++)
        {
            var worker = new RenderWorker(i);
            var thread = new Thread(() =>
            {
                worker.Dispatcher = Dispatcher.CurrentDispatcher;
                worker.Ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "FindFont.Render." + i,
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            worker.Thread = thread;
            workers[i] = worker;
        }

        foreach (var worker in workers)
        {
            if (!worker.Ready.Wait(TimeSpan.FromSeconds(5)) || worker.Dispatcher is null)
                throw new InvalidOperationException("渲染线程启动失败。");
        }

        return workers;
    }

    private void StopWorkers()
    {
        foreach (var worker in _workers)
        {
            try
            {
                worker.Dispatcher?.InvokeShutdown();
            }
            catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException)
            {
                // 线程已退出。
            }

            // 等线程真正退出，避免进程收尾时还有 Dispatcher 在跑窗口消息。
            worker.Thread?.Join(TimeSpan.FromSeconds(1));
        }
    }
}
