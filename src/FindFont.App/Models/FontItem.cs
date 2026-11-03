using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using FindFont.App.Services;

namespace FindFont.App.Models;

/// <summary>字体语言支持推断结果。</summary>
public enum FontLanguage
{
    Unknown = 0,
    Latin = 1,
    Chinese = 2,
    Japanese = 3,
    Korean = 4,
}

/// <summary>字体的筛选分类（侧边栏使用）。</summary>
public enum FontCategory
{
    All = 0,
    Chinese = 1,
    Latin = 2,
    Other = 3,
}

/// <summary>字重/风格筛选。</summary>
public enum FontStyleFilter
{
    All = 0,
    Regular = 1,
    Bold = 2,
    Italic = 3,
}

/// <summary>单个字体的数据模型。UI 只通过位图渲染展示字体，从不绑定 FontFamily。</summary>
public partial class FontItem : ObservableObject
{
    [ObservableProperty] private string _familyName = string.Empty;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _localizedName = string.Empty;
    [ObservableProperty] private string _fileName = string.Empty;
    [ObservableProperty] private string _fullPath = string.Empty;
    [ObservableProperty] private string _styleName = string.Empty;
    [ObservableProperty] private int _weight = 400;
    [ObservableProperty] private bool _isItalic;
    [ObservableProperty] private bool _isBold;
    [ObservableProperty] private FontLanguage _language = FontLanguage.Unknown;
    [ObservableProperty] private string _chineseName = string.Empty;
    [ObservableProperty] private FontCategory _category = FontCategory.All;

    // 渲染缓存状态：BitmapSource 由 FontRenderCache 填入，UI 用 Image 展示。
    // 列表行用 PreviewBitmap（92px 高），选中行展开用 PreviewBitmapLarge（220px 高）。
    [ObservableProperty] private object? _previewBitmap;
    [ObservableProperty] private object? _previewBitmapLarge;
    [ObservableProperty] private bool _isPreviewPending;

    // 健康状态
    [ObservableProperty] private bool _isBroken;
    [ObservableProperty] private bool _isSystemFont = true;
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>
    /// 搜索用的小写拼串（家族名 + 显示名 + 本地化名 + 中文名 + 文件名 + 样式名）。
    /// 每次名称变化都重建一次，让一次滚动/搜索只做一次 Contains。
    /// </summary>
    public string SearchHaystack { get; private set; } = string.Empty;

    /// <summary>字体在所有候选中用于显示的最终名称（本地化名优先）。</summary>
    public string BestName =>
        !string.IsNullOrWhiteSpace(ChineseName) ? ChineseName! :
        !string.IsNullOrWhiteSpace(LocalizedName) ? LocalizedName! :
        DisplayName;

    public bool IsChinese => Language is FontLanguage.Chinese or FontLanguage.Japanese or FontLanguage.Korean;

    partial void OnLocalizedNameChanged(string value) => OnNameChanged();
    partial void OnChineseNameChanged(string value) => OnNameChanged();
    partial void OnDisplayNameChanged(string value) => OnNameChanged();
    partial void OnFamilyNameChanged(string value) => OnNameChanged();
    partial void OnFileNameChanged(string value) => OnNameChanged();
    partial void OnStyleNameChanged(string value) => OnNameChanged();

    private void OnNameChanged()
    {
        OnPropertyChanged(nameof(BestName));
        RebuildHaystack();
    }

    private void RebuildHaystack()
    {
        SearchHaystack = string.Concat(
            FamilyName, "\u0001",
            DisplayName, "\u0001",
            LocalizedName, "\u0001",
            ChineseName, "\u0001",
            FileName, "\u0001",
            StyleName).ToLowerInvariant();
    }

    /// <summary>
    /// 为展开态请求大样本位图。只有真正被选中的行才调用 —— 滚动时绝不触发大图渲染。
    /// </summary>
    public void RequestLargePreview(string text, double fontSize, int width, int height)
    {
        if (string.IsNullOrEmpty(FullPath)) return;

        var request = new RenderRequest(
            FontKey: FullPath,
            FontFilePath: FullPath,
            Text: string.IsNullOrEmpty(text) ? " " : text,
            FontSize: fontSize <= 0 ? 28 : fontSize,
            PixelWidth: width,
            PixelHeight: height);

        var cached = AppServices.RenderCache.TryGetFromCache(request);
        if (cached is not null)
        {
            PreviewBitmapLarge = cached;
            return;
        }

        _ = ApplyLargeAsync(request);
    }

    private async Task ApplyLargeAsync(RenderRequest request)
    {
                try
        {
            var bitmap = await AppServices.RenderCache.RenderAsync(request).ConfigureAwait(true);
            PreviewBitmapLarge = bitmap;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
        }
    }

    /// <summary>丢弃大样本位图，释放内存（取消选中时调用）。</summary>
    public void ReleaseLargePreview() => PreviewBitmapLarge = null;

    public override string ToString() => $"{FamilyName} [{StyleName}]";

    /// <summary>用于去重的稳定键：家族名 + 样式名（忽略大小写）。</summary>
    public string DedupKey => $"{FamilyName}\u0001{StyleName}".ToLowerInvariant();
}

/// <summary>对比窗口的三栏选择结果。</summary>
public sealed class CompareSelection
{
    public FontItem? Left { get; init; }
    public FontItem? Right { get; init; }
    public FontItem? Third { get; init; }
    public ObservableCollection<FontItem> Items { get; } = new();

    public IEnumerable<FontItem> NonNull()
    {
        if (Left is not null) yield return Left;
        if (Right is not null) yield return Right;
        if (Third is not null) yield return Third;
    }
}
