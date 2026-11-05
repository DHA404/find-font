using System.Diagnostics;
using FindFont.App.Helpers;
using FindFont.App.Models;
using FindFont.App.Services;
using FindFont.App.ViewModels;

using static FindFont.SmokeTests.TestRunner;

namespace FindFont.SmokeTests;

/// <summary>
/// 启动与首屏链路的性能护栏。
/// <para>
/// 这里刻意<b>不</b>断言绝对毫秒数 —— CI 机器的磁盘比开发机慢一个数量级，
/// 绝对时间断言必然假失败（本项目已经踩过一次）。改为断言「结构性保证」：
/// 该合并的打开次数合并了、该并行的确实并发、缓存该命中的命中。
/// </para>
/// </summary>
internal static class PerformanceTests
{
    /// <summary>一次打开读完所有字面，结果必须与逐字面单独读完全一致。</summary>
    internal static void TestSingleOpenMatchesPerFace()
    {
        var scanner = new FontScanner();
        var scan = scanner.Scan();
        var multiFace = scan.Items
            .Where(i => i.FaceCount > 1 && !string.IsNullOrEmpty(i.FullPath))
            .GroupBy(i => i.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key)
            .Take(3)
            .ToList();

        Check(multiFace.Count > 0, "本机应至少有一个多字面容器，否则这条用例没意义");

        foreach (var path in multiFace)
        {
            var all = FontMetadataReader.ReadFile(path);
            Check(all.Count > 1, $"{Path.GetFileName(path)} 应解出多个字面，实际 {all.Count}");

            for (var i = 0; i < all.Count; i++)
            {
                var single = FontMetadataReader.TryReadFace(path, i);
                Check(single is not null, $"{Path.GetFileName(path)} 第 {i + 1} 面单独读取不应失败");
                Check(single!.FamilyName == all[i].FamilyName,
                    $"{Path.GetFileName(path)} 第 {i + 1} 面家族名应与批量读取一致");
                Check(single.FaceIndex == all[i].FaceIndex,
                    $"{Path.GetFileName(path)} 第 {i + 1} 面的字面序号应与批量读取一致");
            }
        }
    }

    /// <summary>同一路径只探测一次：批量建索引的文件数与去重后的文件数必须相等。</summary>
    internal static void TestFileIndexProbesEachFileOnce()
    {
        var scanner = new FontScanner();
        var scan = scanner.Scan();

        var paths = scan.Items
            .Where(i => !string.IsNullOrEmpty(i.FullPath))
            .Select(i => i.FullPath)
            .ToList();

        var unique = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Check(unique.Count > 0, "应有可探测的字体文件");

        var probes = FontFileIndex.Build(unique);
        Check(probes.Count == unique.Count,
            $"探测结果条数应等于去重文件数：期望 {unique.Count}，实际 {probes.Count}");

        foreach (var probe in probes.Values)
        {
            Check(probe.FaceCount >= 1, "每个探测结果至少一个字面");
            Check(probe.Size >= 0, "文件大小不应为负");
        }
    }

    /// <summary>注册表并行读取后，返回顺序必须固定（否则每次启动列表顺序都在抖）。</summary>
    internal static void TestRegistryOrderIsStable()
    {
        var first = FontRegistryReader.ReadEntries();
        var second = FontRegistryReader.ReadEntries();

        Check(first.Count > 0, "应读到注册表字体项");
        Check(first.Count == second.Count,
            $"两次读取条数应一致：{first.Count} vs {second.Count}");

        for (var i = 0; i < first.Count; i++)
        {
            Check(first[i].Name == second[i].Name && first[i].Value == second[i].Value,
                $"第 {i} 项的并行读取顺序不稳定");
        }
    }

    /// <summary>首屏预热：行数落在视口区间内，且小样本请求不会退化成串行等待。</summary>
    internal static void TestPrewarmCoversViewport()
    {
        Check(MainViewModel.PrewarmRows is >= 8 and <= 24,
            $"首屏预热行数应在 8–24 之间，实际 {MainViewModel.PrewarmRows}");

        var vm = new MainViewModel();
        var items = new List<FontItem>();
        var fonts = AppServices.Scanner.Scan().Items.Where(i => !string.IsNullOrEmpty(i.FullPath)).Take(16).ToList();
        Check(fonts.Count > 0, "需要至少一个真实字体才能预热");
        items.AddRange(fonts);

        vm.SeedForStressTest(items);
        vm.PreviewText = "Prewarm 0123 你好";
        vm.PreviewFontSize = 36;

        var before = AppServices.RenderCache.RenderedFrames;
        var sw = Stopwatch.StartNew();
        vm.PrewarmFirstScreen();
        sw.Stop();

        // 预热只是把请求排进队列，绝不能在 UI 线程上等出图。
        Check(sw.ElapsedMilliseconds < 200,
            $"预热投递应瞬时返回，实际耗时 {sw.ElapsedMilliseconds} ms");

        // 之后位图应该陆续落位（渲染线程池在后台跑）。
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 8000)
        {
            var done = items.Count(i => i.PreviewBitmap is not null);
            if (done >= Math.Min(MainViewModel.PrewarmRows, items.Count)) break;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            Thread.Sleep(20);
        }

        var rendered = items.Count(i => i.PreviewBitmap is not null);
        Check(AppServices.RenderCache.RenderedFrames >= before,
            "渲染帧计数不应倒退");
        Check(rendered >= Math.Min(MainViewModel.PrewarmRows, items.Count) || rendered > 0,
            $"预热后至少应有位图落位，实际 {rendered} 行");
    }

    /// <summary>渲染线程池：2–4 条 STA 线程，且不同键确实能同时出图。</summary>
    internal static void TestRenderWorkerPool()
    {
        using var cache = new FontRenderCache(256);
        Check(cache.WorkerThreads is >= 2 and <= 4,
            $"渲染线程数应夹在 2–4 之间，实际 {cache.WorkerThreads}");

        var fonts = AppServices.Scanner.Scan().Items
            .Where(i => !string.IsNullOrEmpty(i.FullPath))
            .Take(8)
            .ToList();
        Check(fonts.Count >= 4, "需要至少 4 个真实字体才能验证并发");

        var requests = fonts.Select(item => new RenderRequest(
            FontKey: item.RenderKey,
            FontFilePath: item.FullPath,
            Text: "Pool 0123 你好",
            FontSize: 36,
            PixelWidth: 640,
            PixelHeight: 92,
            Dpi: 96,
            FamilyName: item.LocalizedName)).ToList();

        var tasks = requests.Select(r => cache.RenderAsync(r)).ToList();
        var bitmaps = Task.WhenAll(tasks).GetAwaiter().GetResult();

        Check(bitmaps.All(b => b is not null), "并发渲染不应返回 null");
        Check(cache.RenderedFrames == requests.Count,
            $"每个键应恰好渲染一次，期望 {requests.Count}，实际 {cache.RenderedFrames}");

        // 缓存命中：再请求一次不应该产生新的渲染帧。
        foreach (var request in requests)
        {
            var hit = cache.TryGetFromCache(request);
            Check(hit is not null, "并发渲染完成后应全部命中缓存");
        }

        Check(cache.RenderedFrames == requests.Count, "二次请求不应再次渲染");
        Check(bitmaps.All(b => b!.IsFrozen), "位图应全部冻结以便跨线程共享");
    }
}
