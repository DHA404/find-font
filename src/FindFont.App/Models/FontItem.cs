using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media.Imaging;
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

    // 多字面容器（.ttc/.otc）信息：一个文件里装着多个字体，每个成员是一行。
    [ObservableProperty] private int _faceIndex;
    [ObservableProperty] private int _faceCount = 1;
    [ObservableProperty] private string _embedding = string.Empty;

    // 排序依据：扫描时一次性取到，不在每次筛选时重读文件系统。
    [ObservableProperty] private long _fileSize;
    [ObservableProperty] private DateTime _modifiedAt = DateTime.MinValue;

    /// <summary>同一个字体文件里的第几个字面；单字体文件恒为 0。</summary>
    public bool HasMultipleFaces => FaceCount > 1;

    /// <summary>形如「第 2/3 面」；单字体文件返回空串，界面据此隐藏该标签。</summary>
    public string FaceLabel => FaceCount > 1 ? $"第 {FaceIndex + 1}/{FaceCount} 面" : string.Empty;

    partial void OnFaceIndexChanged(int value) => OnPropertyChanged(nameof(FaceLabel));
    partial void OnFaceCountChanged(int value)
    {
        OnPropertyChanged(nameof(FaceLabel));
        OnPropertyChanged(nameof(HasMultipleFaces));
    }

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

    /// <summary>
    /// 渲染缓存键。同一个 .ttc 文件里的不同字面必须区分开，否则宋体与新宋体会互相
    /// 命中对方的位图。单字体文件保持纯路径，兼容旧缓存。
    /// </summary>
    public string RenderKey => FaceCount > 1 ? $"{FullPath}#{FaceIndex}" : FullPath;

    private void RebuildHaystack()
    {
        var parts = string.Join('\u0001', FamilyName, DisplayName, LocalizedName,
            ChineseName, FileName, StyleName);
        if (FaceCount > 1)
        {
            parts += "\u0001" + FaceLabel + "\u0001" + FamilyName + " " + FaceIndex;
        }
        SearchHaystack = parts.ToLowerInvariant();
    }

    /// <summary>
    /// 为展开态请求大样本位图。只有真正被选中的行才调用 —— 滚动时绝不触发大图渲染。
    /// </summary>
    public void RequestLargePreview(string text, double fontSize, int width, int height)
    {
        var cached = TryTakeCachedSmall(text, fontSize, width, height);
        if (cached is not null)
        {
            PreviewBitmapLarge = cached;
            return;
        }

        var request = BuildRequest(text, fontSize, width, height);
        if (request is null) return;

        _ = ApplyLargeAsync(request);
    }

    /// <summary>
    /// 为列表行请求小样本位图。由首屏预热批量调用，让用户不用先看到一排「正在渲染…」。
    /// 已缓存的直接落位，未缓存的交给渲染线程池并发出图。
    /// </summary>
    public void RequestSmallPreview(string text, double fontSize, int width, int height)
    {
        var cached = TryTakeCachedSmall(text, fontSize, width, height);
        if (cached is not null)
        {
            PreviewBitmap = cached;
            return;
        }

        var request = BuildRequest(text, fontSize, width, height);
        if (request is null) return;

        _ = ApplySmallAsync(request);
    }

    private BitmapSource? TryTakeCachedSmall(string text, double fontSize, int width, int height)
    {
        var request = BuildRequest(text, fontSize, width, height);
        return request is null ? null : AppServices.RenderCache.TryGetFromCache(request);
    }

    private RenderRequest? BuildRequest(string text, double fontSize, int width, int height)
    {
        if (string.IsNullOrEmpty(FullPath)) return null;

        return new RenderRequest(
            FontKey: RenderKey,
            FontFilePath: FullPath,
            Text: string.IsNullOrEmpty(text) ? " " : text,
            FontSize: fontSize <= 0 ? 28 : fontSize,
            PixelWidth: width,
            PixelHeight: height,
            Dpi: RenderDpi.Current,
            FamilyName: string.IsNullOrWhiteSpace(LocalizedName) ? FamilyName : LocalizedName);
    }

    private async Task ApplyLargeAsync(RenderRequest request)
    {
        try
        {
            PreviewBitmapLarge = await AppServices.RenderCache.RenderAsync(request).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
        }
    }

    private async Task ApplySmallAsync(RenderRequest request)
    {
        try
        {
            PreviewBitmap = await AppServices.RenderCache.RenderAsync(request).ConfigureAwait(true);
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

    // ---- 搜索高亮：命中片段在元数据行下方单独成行，命中部分用品格红标出 ----

    [ObservableProperty] private string _matchBefore = string.Empty;
    [ObservableProperty] private string _matchHit = string.Empty;
    [ObservableProperty] private string _matchAfter = string.Empty;

    /// <summary>当前是否有命中片段可显示。</summary>
    public bool HasMatch => MatchHit.Length > 0;

    partial void OnMatchHitChanged(string value) => OnPropertyChanged(nameof(HasMatch));

    /// <summary>
    /// 在原始大小写的展示文本里找命中位置，切成「前 / 命中 / 后」三段。
    /// 找不到（只命中文件名或样式名等次要字段）时清空，不强行造一段。
    /// </summary>
    public void SetHighlight(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            MatchBefore = MatchHit = MatchAfter = string.Empty;
            return;
        }

        foreach (var source in new[] { BestName, LocalizedName, FamilyName, StyleName, FileName })
        {
            if (string.IsNullOrEmpty(source)) continue;
            var index = source.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;

            const int lead = 12;
            const int tail = 28;
            var start = Math.Max(0, index - lead);
            var end = Math.Min(source.Length, index + query.Length + tail);

            MatchBefore = (start > 0 ? "…" : string.Empty) + source[start..index];
            MatchHit = source.Substring(index, query.Length);
            MatchAfter = source[(index + query.Length)..end] + (end < source.Length ? "…" : string.Empty);
            return;
        }

        MatchBefore = MatchHit = MatchAfter = string.Empty;
    }
}

/// <summary>列表排序方式。</summary>
public enum FontSortMode
{
    Name = 0,
    Recent = 1,
    Size = 2,
    Language = 3,
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
