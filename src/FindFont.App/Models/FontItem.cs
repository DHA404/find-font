using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

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
    [ObservableProperty] private object? _previewBitmap;
    [ObservableProperty] private bool _isPreviewPending;

    // 健康状态
    [ObservableProperty] private bool _isBroken;
    [ObservableProperty] private bool _isSystemFont = true;
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>字体在所有候选中用于显示的最终名称（本地化名优先）。</summary>
    public string BestName =>
        !string.IsNullOrWhiteSpace(ChineseName) ? ChineseName! :
        !string.IsNullOrWhiteSpace(LocalizedName) ? LocalizedName! :
        DisplayName;

    public bool IsChinese => Language is FontLanguage.Chinese or FontLanguage.Japanese or FontLanguage.Korean;

    partial void OnLocalizedNameChanged(string value) => OnPropertyChanged(nameof(BestName));
    partial void OnChineseNameChanged(string value) => OnPropertyChanged(nameof(BestName));
    partial void OnDisplayNameChanged(string value) => OnPropertyChanged(nameof(BestName));

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
