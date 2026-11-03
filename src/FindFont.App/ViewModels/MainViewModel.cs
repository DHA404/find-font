using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindFont.App.Models;
using FindFont.App.Services;

namespace FindFont.App.ViewModels;

/// <summary>主窗口视图模型：扫描、搜索防抖、筛选、选中、安装卸载、坏字体清理。</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly List<FontItem> _allFonts = new(1024);
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _enhanceCts;

    private const int SearchDebounceMs = 300;

    /// <summary>
    /// 每次筛选结果落地后触发。视图用它播放入场动画——视图模型不碰任何 UI 类型，
    /// 也不关心订阅者是不是窗口。
    /// </summary>
    public event EventHandler? FilterApplied;

    public ObservableCollection<FontItem> VisibleFonts { get; } = new();

    public ObservableCollection<FontItem> SelectedFonts { get; } = new();

    /// <summary>左栏索引条：分类名 + 该类在「当前其余筛选条件」下的数量。</summary>
    public ObservableCollection<FacetItem> CategoryFacets { get; } = new();

    public IReadOnlyList<string> CategoryOptions { get; } =
        new[] { "全部", "中文", "西文", "其他" };

    public IReadOnlyList<string> StyleOptions { get; } =
        new[] { "全部字重", "常规", "粗体", "斜体" };

    public IReadOnlyList<double> FontSizeOptions { get; } =
        new[] { 20d, 28d, 36d, 48d, 64d, 88d };

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _previewText = "The quick brown fox 0123 你好，世界";
    [ObservableProperty] private double _previewFontSize = 36;
    [ObservableProperty] private int _selectedCategoryIndex;
    [ObservableProperty] private int _selectedStyleIndex;
    [ObservableProperty] private bool _showBrokenOnly;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private double _loadProgress;
    [ObservableProperty] private string _statusText = "准备就绪";
    [ObservableProperty] private FontItem? _focusedFont;

    // 详情面板的结构化字段（替代原来拼成一大段的多行字符串）。
    [ObservableProperty] private string _colophonFamily = string.Empty;
    [ObservableProperty] private string _colophonLocalized = string.Empty;
    [ObservableProperty] private string _colophonStyle = string.Empty;
    [ObservableProperty] private string _colophonWeight = string.Empty;
    [ObservableProperty] private string _colophonFileName = string.Empty;
    [ObservableProperty] private string _colophonPath = string.Empty;
    [ObservableProperty] private string _colophonLanguage = string.Empty;
    [ObservableProperty] private string _colophonStatus = string.Empty;

    public int TotalCount => _allFonts.Count;
    public int BrokenCount => _allFonts.Count(f => f.IsBroken);

    public bool HasSelection => SelectedFonts.Count > 0;
    public bool CanCompare => SelectedFonts.Count >= 2;
    public bool CanUninstall => SelectedFonts.Count == 1 && !SelectedFonts[0].IsBroken;

    /// <summary>对比按钮的可用条件：至少选中 1 个（单字体也允许放大看）。</summary>
    public bool CanCompareOrSingle => SelectedFonts.Count >= 1;

    /// <summary>右栏档案是否可见：没有选中时折叠，只留索引与样本。</summary>
    public bool IsDetailVisible => FocusedFont is not null;

    /// <summary>样本列表为空时的引导文案。</summary>
    public string EmptyStateText => string.IsNullOrWhiteSpace(SearchText)
        ? "这里还没有字体。点「重新扫描」读一遍本机字体注册表。"
        : "没有匹配的字体。换个关键词，或把分类切回「全部」。";

    /// <summary>当前没有任何可见字体（用于显示空状态）。</summary>
    public bool HasNoResults => VisibleFonts.Count == 0;

    // 风格互斥开关：XAML 用四个方块开关表达同一条筛选轴。
    public bool IsStyleAll
    {
        get => SelectedStyleIndex == 0;
        set { if (value) SetStyleIndex(0); }
    }

    public bool IsStyleRegular
    {
        get => SelectedStyleIndex == 1;
        set { if (value) SetStyleIndex(1); }
    }

    public bool IsStyleBold
    {
        get => SelectedStyleIndex == 2;
        set { if (value) SetStyleIndex(2); }
    }

    public bool IsStyleItalic
    {
        get => SelectedStyleIndex == 3;
        set { if (value) SetStyleIndex(3); }
    }

    private void SetStyleIndex(int index)
    {
        SelectedStyleIndex = index;
    }

    private void RaiseStyleFlags()
    {
        OnPropertyChanged(nameof(IsStyleAll));
        OnPropertyChanged(nameof(IsStyleRegular));
        OnPropertyChanged(nameof(IsStyleBold));
        OnPropertyChanged(nameof(IsStyleItalic));
    }

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
        AppServices.RenderCache.Clear();
        RefreshPreviewBitmaps();
    }

    partial void OnPreviewFontSizeChanged(double value)
    {
        AppServices.RenderCache.Clear();
        RefreshPreviewBitmaps();
    }

    private void RefreshPreviewBitmaps()
    {
        foreach (var item in VisibleFonts)
        {
            item.PreviewBitmap = null;
            item.PreviewBitmapLarge = null;
            item.IsPreviewPending = false;
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

        return item.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || item.LocalizedName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || item.ChineseName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || item.FamilyName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || item.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
               || item.StyleName.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    internal void ApplyFilter()
    {
        var matches = _allFonts.Where(Matches).ToList();

        // 批量替换，避免逐条 Add 触发上千次集合变更。
        VisibleFonts.Clear();
        foreach (var item in matches) VisibleFonts.Add(item);

        RebuildFacets();

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
    private void RebuildFacets()
    {
        var counts = new int[4];
        foreach (var item in _allFonts)
        {
            if (!MatchesExceptCategory(item)) continue;
            counts[Math.Clamp((int)item.Category, 0, 3)]++;
        }

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

    [RelayCommand]
    private async Task RefreshAsync()
    {
        AppServices.RenderCache.Clear();
        await LoadAsync();
    }

    [RelayCommand]
    private void SelectFont(FontItem? item)
    {
        if (item is null) return;
        FocusedFont = item;

        var hasCjk = HasCjk(item);
        ColophonFamily = string.IsNullOrWhiteSpace(item.FamilyName) ? item.DisplayName : item.FamilyName;
        ColophonLocalized = string.IsNullOrWhiteSpace(item.LocalizedName) ? "—" : item.LocalizedName;
        ColophonStyle = string.IsNullOrWhiteSpace(item.StyleName) ? "—" : item.StyleName;
        ColophonWeight = $"{item.Weight}";
        ColophonFileName = string.IsNullOrWhiteSpace(item.FileName) ? "—" : item.FileName;
        ColophonPath = string.IsNullOrWhiteSpace(item.FullPath) ? "—" : item.FullPath;
        ColophonLanguage = hasCjk ? "中文 / 东亚字符" : "西文";
        ColophonStatus = item.IsBroken
            ? $"异常   {item.StatusMessage}"
            : string.IsNullOrWhiteSpace(item.StatusMessage) ? "正常" : item.StatusMessage;
    }

    private void ClearColophon()
    {
        ColophonFamily = string.Empty;
        ColophonLocalized = string.Empty;
        ColophonStyle = string.Empty;
        ColophonWeight = string.Empty;
        ColophonFileName = string.Empty;
        ColophonPath = string.Empty;
        ColophonLanguage = string.Empty;
        ColophonStatus = string.Empty;
    }

    /// <summary>由视图层把 ListBox 的多选结果同步到视图模型（UI 线程调用）。</summary>
    public void UpdateSelection(IReadOnlyList<FontItem> selected)
    {
        SelectedFonts.Clear();
        foreach (var item in selected) SelectedFonts.Add(item);

        if (selected.Count == 1) SelectFont(selected[0]);
        else if (selected.Count > 1) FocusedFont = selected[0];
        else FocusedFont = null;

        SelectionChanged();
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
        ApplyFilter();
    }

    [RelayCommand]
    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanCompare));
        OnPropertyChanged(nameof(CanUninstall));
        OnPropertyChanged(nameof(CanCompareOrSingle));
        OnPropertyChanged(nameof(IsDetailVisible));
    }

    [RelayCommand]
    private async Task InstallFromFilesAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要安装的字体文件",
            Filter = "字体文件|*.ttf;*.otf;*.ttc;*.otc|所有文件|*.*",
            Multiselect = true,
        };

        if (dialog.ShowDialog() != true) return;

        StatusText = "正在请求管理员权限安装字体…";
        var result = await FontInstaller.InstallAsync(dialog.FileNames);
        StatusText = result.WasCancelled ? "已取消安装" : result.Log.Trim();

        if (result.Success) await LoadAsync();
    }

    [RelayCommand]
    private async Task UninstallSelectedAsync()
    {
        var target = SelectedFonts.FirstOrDefault();
        if (target is null || target.IsBroken)
        {
            StatusText = "请选择一个可卸载的字体";
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"确定要卸载字体“{target.BestName}”吗？\n文件：{target.FileName}",
            "卸载字体",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        StatusText = "正在请求管理员权限卸载字体…";
        var result = await FontInstaller.UninstallAsync(new[] { $"{target.BestName}|{target.FullPath}" });
        StatusText = result.WasCancelled ? "已取消卸载" : result.Log.Trim();

        if (result.Success) await LoadAsync();
    }

    [RelayCommand]
    private async Task CleanBrokenAsync()
    {
        var broken = _allFonts.Where(f => f.IsBroken).ToList();
        if (broken.Count == 0)
        {
            StatusText = "没有发现异常字体项";
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"发现 {broken.Count} 个异常字体项（指向不存在的文件或无法解析）。\n" +
            "清理只会删除注册表中的无效登记，不会删除任何字体文件。是否继续？",
            "清理异常字体",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        var entries = broken.Select(f => $"{f.DisplayName}|{f.FileName}").ToList();
        StatusText = "正在请求管理员权限清理…";
        var result = await FontInstaller.CleanBrokenAsync(entries);
        StatusText = result.WasCancelled ? "已取消清理" : result.Log.Trim();

        if (result.Success) await LoadAsync();
    }

    [RelayCommand]
    private void OpenFontFolder()
    {
        var item = FocusedFont ?? SelectedFonts.FirstOrDefault();
        if (item is null) return;

        try
        {
            var dir = string.IsNullOrWhiteSpace(item.FullPath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.Fonts)
                : Path.GetDirectoryName(item.FullPath)!;

            if (Directory.Exists(dir))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusText = $"无法打开目录: {ex.Message}";
        }
    }

    /// <summary>压测用：直接注入一份字体集合，跳过注册表扫描。</summary>
    internal void SeedForStressTest(IReadOnlyList<FontItem> items)
    {
        _allFonts.Clear();
        _allFonts.AddRange(items);
        ApplyFilter();
    }

    internal static bool HasCjk(FontItem item) =>
        Helpers.FontMetadataReader.ContainsCjk(item.DisplayName)
        || Helpers.FontMetadataReader.ContainsCjk(item.LocalizedName)
        || item.Category == FontCategory.Chinese;
}
