using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using FindFont.App.Models;
using FindFont.App.Services;

namespace FindFont.App.ViewModels;

/// <summary>用户动作：刷新、选中、安装、卸载、清理、打开目录、关于。</summary>
public partial class MainViewModel
{
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
            : item.IsSystemFont
                ? "系统字体（可清登记，不删文件）"
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

        // 只有被选中的那几行才渲染大样本；上一批被取消选中的行立刻释放。
        ReleaseLargePreviews();
        _lastLargeSelected.AddRange(selected);
        RequestLargePreviews(_lastLargeSelected);

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

        // 用户可能通过「所有文件」选了 WOFF 之类的网页字体。这类文件装进去
        // 系统也认不出来，必须先拦下来并说清原因，而不是假装装成功。
        var accepted = new List<string>();
        var refusals = new List<string>();
        foreach (var file in dialog.FileNames)
        {
            if (FontInstaller.IsSupportedFontFile(file)) accepted.Add(file);
            else refusals.Add(FontInstaller.DescribeInstallRefusal(file));
        }

        if (accepted.Count == 0)
        {
            StatusText = string.Join(" ", refusals);
            return;
        }

        StatusText = "正在请求管理员权限安装字体…";
        var result = await FontInstaller.InstallAsync(accepted);
        var log = result.WasCancelled ? "已取消安装" : result.Log.Trim();
        StatusText = refusals.Count == 0 ? log : $"{log}　（跳过 {refusals.Count} 个不支持的文件）";

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

        var managed = FontInstaller.IsManagedInstall(target.FullPath);

        // 系统自带字体 / 用户手工放进来的字体：只允许清除登记，绝不动文件。
        var detail = managed
            ? $"将从系统字体目录移除文件（进回收站）：\n{target.FileName}"
            : $"这是系统自带或非本工具安装的字体。\n只会清除它的字体登记，不会删除文件。\n\n文件：{target.FileName}";

        var confirm = System.Windows.MessageBox.Show(
            $"确定要卸载字体“{target.BestName}”吗？\n\n{detail}",
            "卸载字体",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        StatusText = $"正在请求管理员权限卸载 {target.FileName}…";
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
