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

        Helpers.StartupTrace.Mark("scan-done");
        _allFonts.Clear();
        _allFonts.AddRange(result.Items);

        ApplyFilter();
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(BrokenCount));

        StatusText = $"已加载 {result.Items.Count} 个字体（注册表项 {result.RegistryEntries}，" +
                     $"缺失文件 {result.MissingFiles}），耗时 {result.Elapsed.TotalMilliseconds:F0} ms";

        IsLoading = false;

        // 扫描一结束就先给首屏可见的那几行出图，用户不用盯着「正在渲染…」等。
        PrewarmFirstScreen();
        Helpers.StartupTrace.Mark("prewarm-queued");

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
            Helpers.StartupTrace.Mark("enhance-done");

            // 字形覆盖检测排在补名之后，刻意不 await：首屏已经可交互，这个可以慢慢量。
            _ = RefreshCoverageAsync();
        }
        catch (OperationCanceledException)
        {
            // 用户重新加载或退出。
        }
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

        // 缺字筛选：未检测的字体在打开这两个开关时一律排除，因为「不知道」不等于「没问题」。
        if (ShowMissingMapOnly && !item.HasMissingFromCmap) return false;
        if (ShowEmptyGlyphOnly && !item.HasEmptyGlyph) return false;

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

        // 排序只影响呈现顺序，不改变筛选结果集合本身。
        if (SelectedSortIndex != 0) matches.Sort(CurrentComparer);

        // 一次 Reset 而不是上千次 Add。
        VisibleFonts.ReplaceAll(matches);

        // 多列布局：把结果按当前列数编成行，列表绑的是 VisibleRows。
        RebuildRows();

        // 新出现的行如果上一次已经量过，直接把缓存结果补上，不用等下一轮检测。
        SyncCoverageToVisible();

        RefreshHighlights();

        RebuildFacets(counts);

        StatusText = string.IsNullOrWhiteSpace(SearchText)
            ? $"共 {_allFonts.Count} 个字体，当前显示 {matches.Count} 个"
            : $"“{SearchText.Trim()}” 匹配 {matches.Count} 个字体";

        if (CoverageHoleCount > 0) StatusText += $"（{CoverageHoleCount} 个缺字）";

        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(HasNoResults));

        SelectedFonts.Clear();
        SelectionChanged();
        SyncTileSelection();

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
