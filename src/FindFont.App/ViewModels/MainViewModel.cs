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

    public ObservableCollection<FontItem> VisibleFonts { get; } = new();

    public ObservableCollection<FontItem> SelectedFonts { get; } = new();

    public IReadOnlyList<string> CategoryOptions { get; } =
        new[] { "全部", "中文", "西文", "其他" };

    public IReadOnlyList<string> StyleOptions { get; } =
        new[] { "全部字重", "常规", "粗体", "斜体" };

    public IReadOnlyList<double> FontSizeOptions { get; } =
        new[] { 16d, 22d, 28d, 36d, 48d, 64d };

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _previewText = "The quick brown fox 0123 你好，世界";
    [ObservableProperty] private double _previewFontSize = 28;
    [ObservableProperty] private int _selectedCategoryIndex;
    [ObservableProperty] private int _selectedStyleIndex;
    [ObservableProperty] private bool _showBrokenOnly;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private double _loadProgress;
    [ObservableProperty] private string _statusText = "准备就绪";
    [ObservableProperty] private string _detailText = string.Empty;
    [ObservableProperty] private FontItem? _focusedFont;

    public int TotalCount => _allFonts.Count;
    public int BrokenCount => _allFonts.Count(f => f.IsBroken);

    public bool HasSelection => SelectedFonts.Count > 0;
    public bool CanCompare => SelectedFonts.Count >= 2;
    public bool CanUninstall => SelectedFonts.Count == 1 && !SelectedFonts[0].IsBroken;

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

        if (!string.IsNullOrWhiteSpace(DetailText)) DetailText = string.Empty;

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

        try
        {
            await AppServices.Scanner.EnhanceAsync(
                snapshot,
                item =>
                {
                    completed++;
                    LoadProgress = Math.Min(1.0, (double)completed / total);

                    // 名字补全后才谈得上重新筛选；整表重排是 O(n log n) 的，
                    // 逐项触发会在几千个字体上退化成 O(n²)，这里做 150ms 节流。
                    var now = DateTime.UtcNow;
                    if (!string.IsNullOrWhiteSpace(SearchText) &&
                        (now - lastFilter).TotalMilliseconds >= 150)
                    {
                        lastFilter = now;
                        ApplyFilter();
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

    partial void OnSearchTextChanged(string value) => DebounceSearch();

    partial void OnSelectedCategoryIndexChanged(int value) => ApplyFilter();

    partial void OnSelectedStyleIndexChanged(int value) => ApplyFilter();

    partial void OnShowBrokenOnlyChanged(bool value) => ApplyFilter();

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
        if (ShowBrokenOnly && !item.IsBroken) return false;

        var category = SelectedCategoryIndex switch
        {
            1 => FontCategory.Chinese,
            2 => FontCategory.Latin,
            3 => FontCategory.Other,
            _ => FontCategory.All,
        };
        if (category != FontCategory.All && item.Category != category) return false;

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

        StatusText = string.IsNullOrWhiteSpace(SearchText)
            ? $"共 {_allFonts.Count} 个字体，当前显示 {matches.Count} 个"
            : $"“{SearchText.Trim()}” 匹配 {matches.Count} 个字体";

        SelectedFonts.Clear();
        SelectionChanged();
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
        DetailText = $"{item.BestName}" +
                     (string.IsNullOrWhiteSpace(item.LocalizedName) ? string.Empty : $"  ·  {item.LocalizedName}") +
                     $"\n样式 {item.StyleName}   字重 {item.Weight}" +
                     $"\n文件 {item.FileName}" +
                     (string.IsNullOrWhiteSpace(item.FullPath) ? string.Empty : $"\n路径 {item.FullPath}") +
                     $"\n语言支持 {(hasCjk ? "中文/东亚字符" : "西文")}" +
                     (item.IsBroken ? $"\n⚠ {item.StatusMessage}" : string.Empty);
    }

    /// <summary>由视图层把 ListBox 的多选结果同步到视图模型（UI 线程调用）。</summary>
    public void UpdateSelection(IReadOnlyList<FontItem> selected)
    {
        SelectedFonts.Clear();
        foreach (var item in selected) SelectedFonts.Add(item);

        if (selected.Count == 1) SelectFont(selected[0]);
        else if (selected.Count > 1) FocusedFont = selected[0];

        SelectionChanged();
    }

    [RelayCommand]
    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanCompare));
        OnPropertyChanged(nameof(CanUninstall));
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
