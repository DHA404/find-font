using FindFont.App.Models;

namespace FindFont.App.ViewModels;

/// <summary>
/// 多列瓦片布局：把筛选结果切成「一行 N 个字体」的行，并管理瓦片的选中语义。
/// <para>
/// 为什么在视图模型里分块而不是用 WrapPanel：WrapPanel 不支持虚拟化，
/// 1380 个字体一次实例化会直接把首屏拖死。这里保持 ListBox 的
/// VirtualizingStackPanel + Recycling，只在数据层把 N 个字体编成一行。
/// </para>
/// </summary>
public partial class MainViewModel
{
    /// <summary>一侧栏外的最小可用宽度（含瓦片间距），决定最多能放几列。</summary>
    private const double TileSlotMin = 340;

    /// <summary>瓦片宽度量化桶。位图按宽度缓存，不做量化会在拖动窗口时渲染几十个尺寸。</summary>
    private const int TileWidthBucket = 32;

    // 与 Tokens.xaml 的 TileGap 保持同一个值：XAML 里的间距是这个数的两倍。
    private const double TileGap = 20;

    // 与 Tokens.xaml 的 PageGutter 同值：行模板左右各留这么多，末列才不会被裁。
    private const double PageGutter = 24;

    /// <summary>选中区间的锚点下标（Shift 连选用）。</summary>
    private int _tileAnchor = -1;

    /// <summary>未选中任何瓦片时的占位宽度，避免第一帧算不出样本尺寸。</summary>
    private const int TileWidthFallback = 320;

    /// <summary>当前列数。窗口变窄会减少，反之增加，上限 3。</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private int _columns = 2;

    /// <summary>当前瓦片样本位图的像素宽度（已量化）。</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private int _tileWidth = TileWidthFallback;

    /// <summary>列表视口宽度，由视图在尺寸变化时告知。</summary>
    private double _listViewportWidth;

    /// <summary>渲染成行的筛选结果。列表绑定这个而不是 VisibleFonts。</summary>
    public Collections.RangeObservableCollection<FontRow> VisibleRows { get; } = new();

    partial void OnColumnsChanged(int value)
    {
        RebuildRows();
        PrewarmFirstScreen();
    }

    /// <summary>把 VisibleFonts 按当前列数重新编行。</summary>
    public void RebuildRows()
    {
        var columns = Math.Clamp(Columns, 1, 4);
        var source = VisibleFonts;
        var rows = new List<FontRow>((source.Count + columns - 1) / columns);
        var tileWidth = TileWidth;

        for (var i = 0; i < source.Count; i += columns)
        {
            var length = Math.Min(columns, source.Count - i);
            var items = new FontItem[length];
            for (var j = 0; j < length; j++)
            {
                var item = source[i + j];
                // 新扫描出来的字体还没被 UpdateTileMetrics 摸过，这里补一次，
                // 否则它们会按 FontItem 的初始宽度渲染，等下次窗口尺寸变化才纠正。
                if (item.TileWidth != tileWidth) item.TileWidth = tileWidth;
                items[j] = item;
            }
            rows.Add(new FontRow(items, columns));
        }

        VisibleRows.ReplaceAll(rows);

        // 行换过以后原来的下标已经指不到同一个字体，锚点作废。
        _tileAnchor = -1;
    }

    /// <summary>
    /// 依据窗口宽度与列表实际宽度重算列数和瓦片宽度。由视图在 SizeChanged 与
    /// 右栏展开/收起时调用。只在数值真的变了时才重建，避免拖动窗口时抖动。
    /// </summary>
    public void UpdateTileMetrics(double windowWidth, double listWidth)
    {
        if (windowWidth <= 0) return;

        // 列数要看列表自己的可用宽度，而不是窗口宽度：右栏「档案」一展开，
        // 列表就窄了，按窗口宽度算会保持三列然后把第三列挤出血。
        var available = listWidth > 0 ? listWidth : Math.Max(0, windowWidth - 208 - PageGutter);
        _listViewportWidth = available;

        // 行模板给 ItemsControl 留了左右各 24px（与 PageGutter 同值），瓦片自己再各留 TileGap/2。
        var inner = Math.Max(0, available - 2 * PageGutter);
        var columns = Math.Clamp((int)((inner + TileGap) / (TileSlotMin + TileGap)), 1, 3);

        // 向下取整到 32px 桶：向上取整会让 N 列的总宽超过可用宽度，最后一列被裁掉。
        var raw = inner / columns - TileGap;
        var width = (int)(raw / TileWidthBucket) * TileWidthBucket;
        width = Math.Max(220, width);

        var widthChanged = width != TileWidth;
        var columnsChanged = columns != Columns;

        TileWidth = width;
        if (columnsChanged) Columns = columns;

        if (widthChanged || columnsChanged)
        {
            // 宽变了旧位图就作废：清空 PreviewBitmap 会让转换器重新求值，
            // 顺手把新尺寸的请求排进渲染队列（在飞的请求有去重，不会重复渲染）。
            foreach (var item in _allFonts)
            {
                item.TileWidth = width;
                item.PreviewBitmap = null;
            }
            PrewarmFirstScreen();
        }
    }

    /// <summary>
    /// 点击瓦片。Ctrl 切换单个，Shift 从锚点到当前连选，否则独占选中。
    /// 选中结果仍写回 SelectedFonts，右侧档案与「对比选中」照旧工作。
    /// </summary>
    public void ToggleTileSelection(FontItem item, bool ctrl, bool shift)
    {
        var index = VisibleFonts.IndexOf(item);
        if (index < 0) return;

        var selected = new List<FontItem>();

        if (shift && _tileAnchor >= 0 && _tileAnchor < VisibleFonts.Count)
        {
            var from = Math.Min(_tileAnchor, index);
            var to = Math.Max(_tileAnchor, index);
            for (var i = from; i <= to; i++) selected.Add(VisibleFonts[i]);
        }
        else if (ctrl)
        {
            selected.AddRange(SelectedFonts);
            var at = selected.IndexOf(item);
            if (at >= 0) selected.RemoveAt(at);
            else selected.Add(item);
            _tileAnchor = index;
        }
        else
        {
            selected.Add(item);
            _tileAnchor = index;
        }

        UpdateSelection(selected);
        SyncTileSelection();
    }

    /// <summary>按 SelectedFonts 重设每个瓦片的选中标记。</summary>
    public void SyncTileSelection()
    {
        var selected = new HashSet<FontItem>(SelectedFonts);
        foreach (var item in _allFonts) item.IsTileSelected = selected.Contains(item);
    }

    /// <summary>清空瓦片选中（Esc 用）。</summary>
    public void ClearTileSelection()
    {
        _tileAnchor = -1;
        foreach (var item in SelectedFonts) item.IsTileSelected = false;
        SelectedFonts.Clear();
        SelectionChanged();
        ClearColophon();
        ReleaseLargePreviews();
    }

    /// <summary>选中全部当前可见的字体（Ctrl+A 用）。</summary>
    public void SelectAllVisible()
    {
        if (VisibleFonts.Count == 0) return;

        UpdateSelection(VisibleFonts.ToList());
        SyncTileSelection();
    }

    /// <summary>视图宽度未知时给一个合理初值，避免第一帧按 0 宽度渲染。</summary>
    public void EnsureTileMetrics(double windowWidth)
    {
        if (_listViewportWidth > 0) return;
        UpdateTileMetrics(windowWidth, 0);
        foreach (var item in _allFonts) item.TileWidth = TileWidth;
    }
}
