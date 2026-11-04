using System.Windows;
using System.Windows.Media;

namespace FindFont.App.Services;

/// <summary>
/// 当前渲染应该用多少 DPI。
///
/// 进程是 PerMonitorV2 感知的（见 app.manifest），所以 DPI 不是常量：
/// 1080p 主屏是 96，4K 笔记本可能是 144/192，拖到另一块屏还会变。
/// 主窗口在 Loaded 与 DpiChanged 时把 <see cref="Current"/> 更新成
/// <c>VisualTreeHelper.GetDpi(window).PixelsPerDip * 96</c>，
/// 渲染请求读这个值 —— 位图按物理像素出，配合 Stretch="None" 才不会被拉糊。
///
/// 注意 CacheKey 里含 DPI：跨屏拖动后 DPI 变了，旧 DPI 的位图不能命中。
/// </summary>
public static class RenderDpi
{
    /// <summary>默认 96（100% 缩放）。</summary>
    public const double Default = 96.0;

    /// <summary>当前 DPI；永远返回正数。</summary>
    public static double Current { get; private set; } = Default;

    /// <summary>跟着某个视觉元素更新 DPI。</summary>
    public static void Track(Visual visual)
    {
        if (visual is null) return;

        try
        {
            var dpi = VisualTreeHelper.GetDpi(visual);
            var value = dpi.PixelsPerDip * Default;
            Set(value);
        }
        catch (InvalidOperationException ex)
        {
            Helpers.Diagnostics.Log(ex, "dpi probe failed");
        }
    }

    /// <summary>直接设置（测试用，也供 DpiChanged 事件走）。</summary>
    public static void Set(double dpi)
    {
        Current = dpi > 0 && !double.IsNaN(dpi) && !double.IsInfinity(dpi) ? dpi : Default;
    }

    /// <summary>从窗口读一次 DPI 并应用。</summary>
    public static void Track(Window window)
    {
        if (window is null) return;
        Track((Visual)window);
    }
}
