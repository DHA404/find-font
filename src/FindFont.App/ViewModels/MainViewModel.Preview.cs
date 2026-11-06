using FindFont.App.Models;
using FindFont.App.Services;

namespace FindFont.App.ViewModels;

/// <summary>
/// 首屏预热与预览位图的生命周期管理。拆出来只为守住单文件 <300 行的规约。
/// <para>
/// 这里的每个方法都遵守同一条纪律：UI 线程只负责投递请求，绝不在线程上等出图。
/// </para>
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// 首屏预热：只给列表最上面若干行请求小样本位图。
    /// <para>
    /// 渲染走多线程池，12 行并发冷出图约 150 ms（串行要 1.1 s）。这里不等结果，
    /// 只是把请求早早排进队列，等虚拟化真正实例化容器时缓存命中。
    /// 多列布局下一行有 Columns 个瓦片，预热按行数算、按瓦片请求。
    /// </para>
    /// </summary>
    internal void PrewarmFirstScreen(int rows = PrewarmRows)
    {
        var text = PreviewText;
        if (string.IsNullOrWhiteSpace(text)) return;

        var size = PreviewFontSize;
        var columns = Math.Max(1, Columns);
        var count = Math.Min(rows * columns, VisibleFonts.Count);
        for (var i = 0; i < count; i++)
        {
            VisibleFonts[i].RequestTilePreview(text, size, TileSampleHeight);
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(EmptyStateText));
        if (_suspendSearch) return;
        DebounceSearch();
    }

    partial void OnSelectedCategoryIndexChanged(int value) => ApplyFilter();

    partial void OnSelectedSortIndexChanged(int value) => ApplyFilter();

    partial void OnSelectedStyleIndexChanged(int value)
    {
        RaiseStyleFlags();
        ApplyFilter();
    }

    partial void OnShowBrokenOnlyChanged(bool value) => ApplyFilter();

    partial void OnFocusedFontChanged(FontItem? value)
    {
        OnPropertyChanged(nameof(IsDetailVisible));
        if (value is null) ClearColophon();
    }

    partial void OnPreviewTextChanged(string value)
    {
        AppServices.PreviewText = value;
        RefreshPreviewBitmaps();

        // 缺字是相对「样品串」而言的：文字一变，之前量的覆盖就作废，防抖后重测。
        DebounceCoverage();
    }

    partial void OnPreviewFontSizeChanged(double value) => RefreshPreviewBitmaps();

    /// <summary>
    /// 预览文字或字号变化：只把已实现行的位图槽位清零，让绑定重新触发渲染。
    /// 刻意不清空位图缓存 —— 缓存键本来就把文本与字号算进去了，旧图会被 LRU 自然淘汰；
    /// 在这里 Clear() 会让每次打字都把上千个字体重新渲染一遍。
    /// </summary>
    private void RefreshPreviewBitmaps()
    {
        foreach (var item in _allFonts)
        {
            if (item.PreviewBitmap is null && item.PreviewBitmapLarge is null) continue;
            item.PreviewBitmap = null;
            item.PreviewBitmapLarge = null;
            item.IsPreviewPending = false;
        }
    }

    private void RequestLargePreviews(IReadOnlyList<FontItem> items)
    {
        foreach (var item in items)
            item.RequestLargePreview(PreviewText, PreviewFontSize, LargePreviewWidth, LargePreviewHeight);
    }

    /// <summary>回收大样本位图：既有本批选中的，也有上一批残留的。</summary>
    private void ReleaseLargePreviews()
    {
        foreach (var item in SelectedFonts) item.ReleaseLargePreview();
        foreach (var item in _lastLargeSelected) item.ReleaseLargePreview();
        _lastLargeSelected.Clear();
    }

}
