using System.Diagnostics;

using static FindFont.SmokeTests.TestRunner;

namespace FindFont.SmokeTests;

/// <summary>视图模型：加载筛选、压测、对比栏、集合通知与搜索等价性。</summary>
internal static class ViewModelTests
{
    internal static void TestViewModelFilter()
    {
        EnsureApp();
        var vm = new FindFont.App.ViewModels.MainViewModel();
        vm.LoadAsync().GetAwaiter().GetResult();

        var total = vm.VisibleFonts.Count;
        Console.WriteLine($"        无筛选可见 {total} 项");
        Check(total > 0, "加载后应有可见字体");

        vm.SelectedCategoryIndex = 1; // 中文
        Check(vm.VisibleFonts.All(f => f.Category == FontCategory.Chinese), "中文筛选应只留中文类");
        var chinese = vm.VisibleFonts.Count;
        Console.WriteLine($"        中文分类 {chinese} 项");
        Check(chinese > 0 && chinese < total, "中文类应为非空真子集");

        vm.SelectedCategoryIndex = 0;
        vm.SearchText = "arial";
        Check(vm.VisibleFonts.Count > 0, "搜索 arial 应有结果");
        Check(vm.VisibleFonts.All(f =>
                f.DisplayName.Contains("arial", StringComparison.OrdinalIgnoreCase)
                || f.LocalizedName.Contains("arial", StringComparison.OrdinalIgnoreCase)
                || f.ChineseName.Contains("arial", StringComparison.OrdinalIgnoreCase)
                || f.FamilyName.Contains("arial", StringComparison.OrdinalIgnoreCase)
                || f.FileName.Contains("arial", StringComparison.OrdinalIgnoreCase)
                || f.StyleName.Contains("arial", StringComparison.OrdinalIgnoreCase)),
            "搜索结果必须全部命中断词");
        Console.WriteLine($"        搜索 arial 命中 {vm.VisibleFonts.Count} 项");

        vm.SearchText = "zzzz-不存在的字体";
        Check(vm.VisibleFonts.Count == 0, "无意义查询应返回空集");

        vm.SearchText = string.Empty;
        Check(vm.VisibleFonts.Count == total, "清空搜索应恢复全量");

        vm.SelectedStyleIndex = 2; // 粗体
        Check(vm.VisibleFonts.All(f => f.IsBold), "粗体筛选应只留粗体");
        vm.SelectedStyleIndex = 0;
    }

    internal static void TestStressFilter()
    {
        EnsureApp();
        const int count = 10000;
        var items = new List<FontItem>(count);
        for (var i = 0; i < count; i++)
        {
            var isChinese = i % 3 == 0;
            items.Add(new FontItem
            {
                FamilyName = $"Stress Family {i}",
                DisplayName = isChinese ? $"压测字体 {i}" : $"Stress Font {i}",
                LocalizedName = $"Stress Family {i}",
                FileName = $"stress_{i}.ttf",
                StyleName = i % 4 == 0 ? "Bold" : "Regular",
                Weight = i % 4 == 0 ? 700 : 400,
                Category = isChinese ? FontCategory.Chinese : FontCategory.Latin,
            });
        }

        var vm = new FindFont.App.ViewModels.MainViewModel();
        var seed = Stopwatch.StartNew();
        vm.SeedForStressTest(items);
        seed.Stop();
        Check(vm.VisibleFonts.Count == count, $"无筛选时应可见全部 {count} 项");

        var sw = Stopwatch.StartNew();
        vm.SearchText = "stress family 42";
        sw.Stop();
        Check(vm.VisibleFonts.Count > 0, "压测搜索应有命中");
        var searchMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        vm.SelectedCategoryIndex = 1;
        sw.Stop();
        var chineseMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        vm.SelectedCategoryIndex = 0;
        vm.SearchText = string.Empty;
        sw.Stop();
        var resetMs = sw.Elapsed.TotalMilliseconds;

        Console.WriteLine($"        装载 {seed.Elapsed.TotalMilliseconds:F0} ms，" +
                          $"搜索 {searchMs:F0} ms，中文筛选 {chineseMs:F0} ms，恢复 {resetMs:F0} ms");

        Check(searchMs < 500, $"1 万条搜索耗时应 < 500 ms，实际 {searchMs:F0} ms");
        Check(chineseMs < 500, $"1 万条筛选耗时应 < 500 ms，实际 {chineseMs:F0} ms");
        Check(vm.VisibleFonts.Count == count, "清空条件后应恢复全量");
    }

    internal static void TestCompareViewModel()
    {
        var fonts = Enumerable.Range(1, 5)
            .Select(i => new FontItem { FamilyName = $"Font {i}", DisplayName = $"字体 {i}", StyleName = "Regular" })
            .ToList();

        var vm = new FindFont.App.ViewModels.CompareViewModel();
        vm.Load(fonts);
        Check(vm.ColumnCount == FindFont.App.ViewModels.CompareViewModel.MaxColumns,
            $"对比窗口最多 {FindFont.App.ViewModels.CompareViewModel.MaxColumns} 栏，实际 {vm.ColumnCount}");
        Check(!vm.IsEmpty, "加载后不应为空");

        vm.RemoveCommand.Execute(vm.Items[0]);
        Check(vm.ColumnCount == 2, "移除一栏后应剩 2 栏");

        vm.RemoveCommand.Execute(vm.Items[0]);
        vm.RemoveCommand.Execute(vm.Items[0]);
        Check(vm.IsEmpty, "移除全部后应为空");
    }

    /// <summary>逐条 Add 一千次会让 ListBox 重建视图一千次。</summary>
    internal static void TestRangeCollection()
    {
        var collection = new FindFont.App.Collections.RangeObservableCollection<int>();
        var events = 0;
        System.Collections.Specialized.NotifyCollectionChangedAction? lastAction = null;
        collection.CollectionChanged += (_, e) =>
        {
            events++;
            lastAction = e.Action;
        };

        var items = Enumerable.Range(0, 1000).ToArray();
        collection.ReplaceAll(items);

        Check(events == 1, $"ReplaceAll 只应触发一次集合通知，实际 {events} 次");
        Check(lastAction == System.Collections.Specialized.NotifyCollectionChangedAction.Reset, "通知应是 Reset");
        Check(collection.Count == 1000, "内容应全部替换进去");

        collection.ReplaceAll(Array.Empty<int>());
        Check(collection.Count == 0, "ReplaceAll 空集合应清空");
    }

    /// <summary>搜索快路径（单次 Contains 打穿拼串）必须与原来的六字段逐项匹配完全等价。</summary>
    internal static void TestSearchParity()
    {
        var scanner = new FontScanner();
        var items = scanner.Scan().Items.Take(200).ToList();
        Check(items.Count > 0, "应能扫到字体用于比较");

        var queries = new[] { "sans", "宋", "ARIAL", "regular", "noto", "黑" };

        foreach (var query in queries)
        {
            var fast = items.Where(i => FastMatch(i, query)).Count();
            var legacy = items.Where(i => LegacyMatch(i, query)).Count();
            Check(fast == legacy, $"“{query}”的匹配数应一致：快路径 {fast}，原逻辑 {legacy}");
        }
    }

    private static bool FastMatch(FontItem item, string query) =>
        item.SearchHaystack.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static bool LegacyMatch(FontItem item, string query) =>
        item.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || item.LocalizedName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || item.ChineseName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || item.FamilyName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || item.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
        || item.StyleName.Contains(query, StringComparison.CurrentCultureIgnoreCase);
}
