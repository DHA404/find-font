using System.Collections.ObjectModel;
using FindFont.App.Collections;
using FindFont.App.Models;
using FindFont.App.Services;

namespace FindFont.App.ViewModels;

/// <summary>扫描结果落地、搜索防抖、筛选与计数。拆出来只为守住单文件 <300 行的规约。</summary>
public partial class MainViewModel
{
    public async Task LoadAsync()
    {
        if (IsLoading) return;

        IsLoading = true;
        StatusText = "正在读取字体注册表…";
        LoadProgress = 0;

        FontScanResult result;
        try
        {
            result = await Task.Run(AppServices.Scanner.Scan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            StatusText = $"扫描失败: {ex.Message}";
            IsLoading = false;
            return;
        }

        _allFonts.Clear();
        _allFonts.AddRange(result.Items);

        ApplyFilter();
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(BrokenCount));

        StatusText = $"已加载 {result.Items.Count} 个字体（注册表项 {result.RegistryEntries}，" +
                     $"缺失文件 {result.MissingFiles}），耗时 {result.Elapsed.TotalMilliseconds:F0} ms";

        IsLoading = false;

        // 后台补全真实名称：每完成一项就回填 UI。
        _enhanceCts?.Cancel();
        _enhanceCts = new CancellationTokenSource();
        var token = _enhanceCts.Token;

        var snapshot = _allFonts.ToList();
        var completed = 0;
        var total = Math.Max(1, snapshot.Count);

        var progress = new Progress<int>(count =>
        {
            completed = count;
            LoadProgress = (double)count / total;
        });

        var lastFilter = DateTime.UtcNow;

        // EnhanceAsync 的回调跑在线程池线程上，而 ApplyFilter 会改 ObservableCollection。
        // ObservableCollection 只能在 UI 线程改，否则绑定直接抛 NotSupportedException。
        var ui = SynchronizationContext.Current;

        try
        {
            await AppServices.Scanner.EnhanceAsync(
                snapshot,
                item =>
                {
                    completed++;

                    // 名字补全后才谈得上重新筛选；整表重排是 O(n log n) 的，
                    // 逐项触发会在几千个字体上退化成 O(n²)，这里做 150ms 节流。
                    var now = DateTime.UtcNow;
                    if (!string.IsNullOrWhiteSpace(SearchText) &&
                        (now - lastFilter).TotalMilliseconds >= 150)
                    {
                        lastFilter = now;
                        if (ui is null) ApplyFilter();
                        else ui.Post(_ => ApplyFilter(), null);
                    }
                },
                progress,
                token);

            ApplyFilter();
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(BrokenCount));
            StatusText = $"字体信息补全完成：共 {_allFonts.Count} 个字体，异常 {BrokenCount} 个";
        }
        catch (OperationCanceledException)
        {
            // 用户重新加载或退出。
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(EmptyStateText));
        if (_suspendSearch) return;
        DebounceSearch();
    }

    partial void OnSelectedCategoryIndexChanged(int value) => ApplyFilter();

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

    private void ReleaseLargePreviews()
    {
        foreach (var item in _lastLargeSelected) item.ReleaseLargePreview();
        _lastLargeSelected.Clear();
    }

    private void DebounceSearch()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        // 无 UI 同步上下文（例如冒烟测试里直接 new 出视图模型）时退化为同步过滤，
        // 避免 TaskScheduler.FromCurrentSynchronizationContext() 抛异常。
        var context = SynchronizationContext.Current;
        if (context is null)
        {
            ApplyFilter();
            return;
        }

        _ = Task.Delay(SearchDebounceMs, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            context.Post(_ =>
            {
                if (!token.IsCancellationRequested) ApplyFilter();
            }, null);
        }, token, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private bool Matches(FontItem item)
    {
        var category = SelectedCategoryIndex switch
        {
            1 => FontCategory.Chinese,
            2 => FontCategory.Latin,
            3 => FontCategory.Other,
            _ => FontCategory.All,
        };
        if (category != FontCategory.All && item.Category != category) return false;

        return MatchesExceptCategory(item);
    }

    /// <summary>除分类之外的其余筛选条件（搜索 / 风格 / 只看失效）。</summary>
    private bool MatchesExceptCategory(FontItem item)
    {
        if (ShowBrokenOnly && !item.IsBroken) return false;

        var style = SelectedStyleIndex switch
        {
            1 => FontStyleFilter.Regular,
            2 => FontStyleFilter.Bold,
            3 => FontStyleFilter.Italic,
            _ => FontStyleFilter.All,
        };
        if (style != FontStyleFilter.All)
        {
            var ok = style switch
            {
                FontStyleFilter.Bold => item.IsBold,
                FontStyleFilter.Italic => item.IsItalic,
                FontStyleFilter.Regular => !item.IsBold && !item.IsItalic,
                _ => true,
            };
            if (!ok) return false;
        }

        var query = SearchText?.Trim();
        if (string.IsNullOrEmpty(query)) return true;

        // 一次 Contains 打穿六个字段的预先拼好的串（FontItem 在名称变化时重建）。
        return item.SearchHaystack.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    internal void ApplyFilter()
    {
        // 一次遍历同时产出结果集与左栏分类计数，避免对全表扫两遍。
        var matches = new List<FontItem>(_allFonts.Count);
        var counts = new int[4];

        foreach (var item in _allFonts)
        {
            if (!MatchesExceptCategory(item))
                continue;

            counts[Math.Clamp((int)item.Category, 0, 3)]++;

            var category = SelectedCategoryIndex switch
            {
                1 => FontCategory.Chinese,
                2 => FontCategory.Latin,
                3 => FontCategory.Other,
                _ => FontCategory.All,
            };
            if (category == FontCategory.All || item.Category == category)
                matches.Add(item);
        }

        // 一次 Reset 而不是上千次 Add。
        VisibleFonts.ReplaceAll(matches);

        RebuildFacets(counts);

        StatusText = string.IsNullOrWhiteSpace(SearchText)
            ? $"共 {_allFonts.Count} 个字体，当前显示 {matches.Count} 个"
            : $"“{SearchText.Trim()}” 匹配 {matches.Count} 个字体";

        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(HasNoResults));

        SelectedFonts.Clear();
        SelectionChanged();

        // 结果集已经稳定，通知视图播放入场。
        FilterApplied?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 重建左栏索引计数。计数用「除分类以外的其余条件」，这样点进去的数量与结果完全一致。
    /// </summary>
    private void RebuildFacets(int[] counts)
    {
        var total = counts[0] + counts[1] + counts[2] + counts[3];

        // 原地更新计数，绝不 Clear：清空集合会让绑定到 CategoryFacets 的 ListBox
        // 把 SelectedIndex 写回 -1，把用户刚点的分类打回去，集合还会被重绑一遍。
        var values = new[] { total, counts[1], counts[2], counts[3] };
        if (CategoryFacets.Count != values.Length)
        {
            CategoryFacets.Clear();
            foreach (var name in CategoryOptions) CategoryFacets.Add(new FacetItem(name, 0));
        }

        for (var i = 0; i < values.Length; i++) CategoryFacets[i].Count = values[i];
    }
}
