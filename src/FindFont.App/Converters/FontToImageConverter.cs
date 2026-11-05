using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using FindFont.App.Models;
using FindFont.App.Services;

namespace FindFont.App.Converters;

/// <summary>
/// 把 <see cref="FontItem"/> + 预览文本 渲染为 <see cref="BitmapSource"/>。
/// 这是全项目唯一允许的“显示字体”路径（计划书规约 1：XAML 里禁止绑定 FontFamily）。
///
/// MultiBinding 约定（顺序）：[0]=FontItem, [1]=预览文本, [2]=字号(可选)
/// </summary>
public sealed class FontToImageConverter : IMultiValueConverter
{
    /// <summary>渲染卡片高度（像素，96 DPI）。</summary>
    public int PixelHeight { get; set; } = 56;

    /// <summary>渲染卡片宽度。</summary>
    public int PixelWidth { get; set; } = 720;

    /// <summary>默认字号。</summary>
    public double DefaultFontSize { get; set; } = 28;

    public object? Convert(object[] values, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is not FontItem item) return null;

        // values[3] 是 FontItem.PreviewBitmap：非空说明渲染已完成，直接交回 UI。
        if (values.Length > 3 && values[3] is BitmapSource done) return done;

        var text = values.Length > 1 ? values[1] as string : null;
        if (string.IsNullOrEmpty(text)) text = "The quick brown fox 0123";

        var fontSize = DefaultFontSize;
        if (values.Length > 2 && values[2] is IConvertible convertible)
        {
            try { fontSize = convertible.ToDouble(CultureInfo.InvariantCulture); }
            catch (FormatException) { fontSize = DefaultFontSize; }
            catch (InvalidCastException) { fontSize = DefaultFontSize; }
        }

        if (fontSize <= 0) fontSize = DefaultFontSize;

        var width = PixelWidth;
        var height = PixelHeight;
        if (parameter is string p && p.Contains('x'))
        {
            var parts = p.Split('x');
            if (parts.Length == 2
                && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h)
                && w > 0 && h > 0)
            {
                width = w;
                height = h;
            }
        }

        // 高样本（>=120px）用于选中行展开，缓存槽位与大图分开，避免互相覆盖。
        var isLarge = height >= 120;

        var request = new RenderRequest(
            FontKey: string.IsNullOrEmpty(item.FullPath) ? item.FamilyName : item.RenderKey,
            FontFilePath: string.IsNullOrEmpty(item.FullPath) ? null : item.FullPath,
            Text: text,
            FontSize: fontSize,
            PixelWidth: width,
            PixelHeight: height,
            Dpi: RenderDpi.Current,
            FamilyName: string.IsNullOrWhiteSpace(item.LocalizedName) ? item.FamilyName : item.LocalizedName);

        // 先尝试同步命中缓存，未命中时立即触发后台渲染并在完成后回填。
        var cached = AppServices.RenderCache.TryGetFromCache(request);
        if (cached is not null) return cached;

        _ = RenderAndApplyAsync(item, request, isLarge);
        return null;
    }

    private static async Task RenderAndApplyAsync(FontItem item, RenderRequest request, bool isLarge)
    {
        try
        {
            item.IsPreviewPending = true;
            var bitmap = await AppServices.RenderCache.RenderAsync(request).ConfigureAwait(true);
            if (isLarge) item.PreviewBitmapLarge = bitmap;
            else item.PreviewBitmap = bitmap;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            // 渲染被取消（列表已回收），保持占位状态即可。
        }
        finally
        {
            item.IsPreviewPending = false;
        }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
