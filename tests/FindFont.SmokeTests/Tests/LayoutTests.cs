using static FindFont.SmokeTests.TestRunner;

namespace FindFont.SmokeTests;

/// <summary>多列瓦片布局：分行、列数收敛、选中语义。</summary>
internal static class LayoutTests
{
    /// <summary>分行不能丢项也不能重排：拼回去必须与筛选结果逐项一致。</summary>
    internal static void TestRowChunking()
    {
        var vm = new FindFont.App.ViewModels.MainViewModel();
        var items = BuildItems(37);
        vm.SeedForStressTest(items);

        vm.Columns = 2;
        var rows = vm.VisibleRows;
        Check(rows.Count == 19, $"37 条按 2 列应切成 19 行，实际 {rows.Count}");

        var flat = new List<FindFont.App.Models.FontItem>();
        foreach (var row in rows) flat.AddRange(row.Items);
        Check(flat.Count == items.Count, "分行后总条数不应变化");
        for (var i = 0; i < items.Count; i++)
        {
            if (!ReferenceEquals(flat[i], items[i]))
                throw new InvalidOperationException($"第 {i} 项顺序被打乱");
        }

        // 末行不满列数：37 = 18*2 + 1
        Check(rows[^1].Items.Count == 1, "末行应只有 1 个瓦片");

        vm.Columns = 3;
        Check(vm.VisibleRows.Count == 13, $"37 条按 3 列应切成 13 行，实际 {vm.VisibleRows.Count}");

        vm.Columns = 1;
        Check(vm.VisibleRows.Count == 37, "1 列时行数应等于条数");
    }

    /// <summary>列数由窗口宽度决定并夹在 1..3，瓦片宽度落在 32px 桶内。</summary>
    internal static void TestColumnConvergence()
    {
        var vm = new FindFont.App.ViewModels.MainViewModel();
        vm.SeedForStressTest(BuildItems(10));

        // 1360 宽 -> 可用 1130 -> 3 列
        vm.UpdateTileMetrics(1360, 1120);
        Check(vm.Columns == 3, $"1360 宽应给 3 列，实际 {vm.Columns}");
        Check(vm.TileWidth >= 220 && vm.TileWidth % 32 == 0,
            $"瓦片宽度应 >=220 且对齐 32，实际 {vm.TileWidth}");

        // 1080 宽 -> 可用 850 -> 2 列
        vm.UpdateTileMetrics(1080, 852);
        Check(vm.Columns == 2, $"1080 宽应给 2 列，实际 {vm.Columns}");
        Check(vm.TileWidth >= 220 && vm.TileWidth % 32 == 0,
            $"瓦片宽度应 >=220 且对齐 32，实际 {vm.TileWidth}");

        // 700 宽 -> 可用 470 -> 1 列
        vm.UpdateTileMetrics(700, 470);
        Check(vm.Columns == 1, $"700 宽应给 1 列，实际 {vm.Columns}");
        Check(vm.TileWidth >= 220, $"1 列时瓦片宽度应 >=220，实际 {vm.TileWidth}");

        // 同一宽度重复调用不应换档（宽度量化到 32px 桶）
        vm.UpdateTileMetrics(1360, 1120);
        var before = vm.TileWidth;
        vm.UpdateTileMetrics(1361, 1121);
        Check(vm.TileWidth == before, $"宽度微变落在同一 32px 桶内时不应换档（{before} -> {vm.TileWidth}）");
    }

    /// <summary>瓦片点击的选中语义：独占、Ctrl 切换、Shift 连选，并与 SelectedFonts 一致。</summary>
    internal static void TestTileSelection()
    {
        var vm = new FindFont.App.ViewModels.MainViewModel();
        var items = BuildItems(6);
        vm.SeedForStressTest(items);

        vm.ToggleTileSelection(items[1], ctrl: false, shift: false);
        Check(vm.SelectedFonts.Count == 1 && ReferenceEquals(vm.SelectedFonts[0], items[1]),
            "独占点击应只选中被点的那个");
        Check(items[1].IsTileSelected && !items[0].IsTileSelected, "选中标记应落在被点的瓦片上");

        vm.ToggleTileSelection(items[3], ctrl: true, shift: false);
        Check(vm.SelectedFonts.Count == 2, $"Ctrl 应追加成 2 个，实际 {vm.SelectedFonts.Count}");
        Check(items[3].IsTileSelected, "追加的瓦片应被标记选中");

        vm.ToggleTileSelection(items[1], ctrl: true, shift: false);
        Check(vm.SelectedFonts.Count == 1 && items[1].IsTileSelected == false,
            "Ctrl 点已选中的瓦片应取消它");

        // Shift 连选：锚点还在 1，当前 3 -> 1..3 三项
        vm.ToggleTileSelection(items[1], ctrl: false, shift: false);
        vm.ToggleTileSelection(items[4], ctrl: false, shift: true);
        Check(vm.SelectedFonts.Count == 4, $"Shift 应选中 1..4 共 4 个，实际 {vm.SelectedFonts.Count}");
        for (var i = 1; i <= 4; i++) Check(items[i].IsTileSelected, $"第 {i} 个应被选中");

        vm.ClearTileSelection();
        Check(vm.SelectedFonts.Count == 0, "清空后不应还有选中项");
        Check(items.All(i => !i.IsTileSelected), "清空后所有瓦片标记都应复位");
    }

    private static List<FindFont.App.Models.FontItem> BuildItems(int count)
    {
        var list = new List<FindFont.App.Models.FontItem>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(new FindFont.App.Models.FontItem
            {
                FamilyName = $"Family{i:D3}",
                DisplayName = $"Family{i:D3}",
                FileName = $"family{i:D3}.ttf",
                FullPath = $@"C:\Fonts\family{i:D3}.ttf",
                StyleName = "Regular",
                Category = FindFont.App.Models.FontCategory.Latin,
            });
        }

        return list;
    }
}
