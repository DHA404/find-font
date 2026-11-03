using System.Windows.Media.Imaging;
using FindFont.App.Services;

namespace FindFont.App;

/// <summary>极简服务定位器（避免引入完整的 DI 容器，保持单文件体积）。</summary>
public static class AppServices
{
    private static readonly Lazy<FontRenderCache> Cache = new(() => new FontRenderCache(800));

    public static FontRenderCache RenderCache => Cache.Value;

    public static FontScanner Scanner { get; } = new();

    /// <summary>当前全局预览文本（由主窗口输入框写入，供对比窗口复用）。</summary>
    public static string PreviewText { get; set; } = "The quick brown fox 0123 你好，世界";
}
