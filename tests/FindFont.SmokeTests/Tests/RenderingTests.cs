using static FindFont.SmokeTests.TestRunner;

namespace FindFont.SmokeTests;

/// <summary>离屏渲染、位图缓存预算与按需大图。</summary>
internal static class RenderingTests
{
    internal static void TestRenderCache()
    {
        using var cache = new FontRenderCache(capacity: 16);
        var item = new FontItem { FamilyName = "Segoe UI", FullPath = string.Empty };
        var request = new RenderRequest(item.DedupKey, null, "Hello 你好 0123", 32, 480, 56);
        var result = cache.RenderAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        Check(result is not null, "渲染结果不应为 null");
        if (result is null) return;
        var bitmap = result;
        Check(bitmap.Width > 0 && bitmap.Height > 0, "位图尺寸应大于 0");
        Check(bitmap.IsFrozen, "跨线程使用的位图必须 Freeze");

        var again = cache.TryGetFromCache(request);
        Check(again is not null, "第二次请求应命中缓存");
    }

    /// <summary>缓存不能只靠条数兜底：一张 1500x220 的图就是 1.3 MB，条数 × 体积会吃穿内存。</summary>
    internal static void TestCacheByteBudget()
    {
        using var cache = new FontRenderCache(capacity: 500, byteBudgetBytes: 32L * 1024 * 1024);
        Check(cache.ByteBudget <= 32L * 1024 * 1024, "字节预算应被采纳");

        var text = new string('W', 40);
        var tasks = new List<Task>();
        for (var i = 0; i < 40; i++)
        {
            var request = new RenderRequest(
                FontKey: "budget-" + i,
                FontFilePath: null,
                Text: text,
                FontSize: 40,
                PixelWidth: 1500,
                PixelHeight: 220);
            tasks.Add(cache.RenderAsync(request));
        }

        Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(30));

        Check(cache.Bytes <= cache.ByteBudget + 1500 * 220 * 4,
            $"缓存字节数应受预算约束，实际 {cache.Bytes / 1024} KB");
        Check(cache.Count < 40, $"40 张 1500x220 位图不应全部留住，实际留了 {cache.Count} 张");
        Check(cache.Count >= 1, "至少应保留最近一张");
    }

    /// <summary>滚动时同一行会被反复求值：同一 CacheKey 的并发请求必须合并成一次真实渲染。</summary>
    internal static void TestCacheDedupesInFlight()
    {
        using var cache = new FontRenderCache();
        var request = new RenderRequest(
            FontKey: "dedupe",
            FontFilePath: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "arial.ttf"),
            Text: "Dedupe 0123",
            FontSize: 32,
            PixelWidth: 640,
            PixelHeight: 92);

        var before = cache.RenderedFrames;
        var tasks = new Task<System.Windows.Media.Imaging.BitmapSource>[16];
        for (var i = 0; i < tasks.Length; i++) tasks[i] = cache.RenderAsync(request);
        Task.WaitAll(tasks, TimeSpan.FromSeconds(30));

        var first = tasks[0].Result;
        foreach (var task in tasks)
        {
            Check(ReferenceEquals(task.Result, first), "同一键的所有请求应拿到同一张位图");
        }

        var rendered = cache.RenderedFrames - before;
        Check(rendered == 1, $"16 个并发同键请求只应渲染 1 次，实际 {rendered} 次");
        Check(first.IsFrozen, "返回的位图必须已 Freeze 才能跨线程共享");
    }

    /// <summary>滚动经过的行只该渲染小图；大图必须等到真正被选中才渲染。</summary>
    internal static void TestNoLargePreviewWhenIdle()
    {
        var vm = new MainViewModel();
        var arial = new FontItem
        {
            FamilyName = "Arial",
            DisplayName = "Arial",
            FileName = "arial.ttf",
            FullPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "arial.ttf"),
            StyleName = "Regular",
        };
        var simsun = new FontItem
        {
            FamilyName = "SimSun",
            DisplayName = "SimSun",
            FileName = "simsun.ttc",
            FullPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "simsun.ttc"),
            StyleName = "Regular",
        };

        vm.SeedForStressTest(new[] { arial, simsun });
        vm.ApplyFilter();

        // 渲染小图（模拟列表行被实现）
        var small = new RenderRequest(arial.FullPath, arial.FullPath, vm.PreviewText, vm.PreviewFontSize, 1120, 92);
        var smallBitmap = AppServices.RenderCache.RenderAsync(small).GetAwaiter().GetResult();
        arial.PreviewBitmap = smallBitmap;
        simsun.PreviewBitmap = smallBitmap;

        Check(arial.PreviewBitmapLarge is null, "未选中的行不应有大样本位图");
        Check(simsun.PreviewBitmapLarge is null, "未选中的行不应有大样本位图");

        vm.UpdateSelection(new[] { arial });
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (arial.PreviewBitmapLarge is null && DateTime.UtcNow < deadline)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            Thread.Sleep(20);
        }

        Check(arial.PreviewBitmapLarge is not null, "选中之后应渲染出大样本");
        Check(simsun.PreviewBitmapLarge is null, "只该给被选中的那一行渲染大样本");

        vm.UpdateSelection(Array.Empty<FontItem>());
        Check(arial.PreviewBitmapLarge is null, "取消选中后应释放大样本位图");
    }
}
