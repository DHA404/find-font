using CommunityToolkit.Mvvm.ComponentModel;

namespace FindFont.App.Models;

/// <summary>
/// 瓦片网格相关的状态：多列布局下每个字体是一个瓦片，选中标记与样本宽度
/// 都由视图模型统一驱动。拆成 partial 只为守住单文件 300 行的规约。
/// </summary>
public partial class FontItem
{
    // ---- 瓦片网格：一行多个字体，每个瓦片按当前列宽单独渲染 ----

    /// <summary>瓦片是否处于选中状态。选中由 ViewModel 统一同步，ListBox 不参与。</summary>
    [ObservableProperty] private bool _isTileSelected;

    /// <summary>该瓦片样本位图的像素宽度（由列宽决定，量化到 32px 桶）。</summary>
    [ObservableProperty] private int _tileWidth = 500;

    /// <summary>
    /// 请求瓦片样本位图。与 RequestSmallPreview 的差别只在尺寸由 TileWidth 决定，
    /// 缓存查找与落位逻辑完全复用，避免两条渲染路径各自演化。
    /// </summary>
    public void RequestTilePreview(string text, double fontSize, int height)
    {
        var width = TileWidth;
        if (width < 32 || height < 16) return;

        if (TryTakeCachedSmall(text, fontSize, width, height) is { } cached)
        {
            PreviewBitmap = cached;
            return;
        }

        var request = BuildRequest(text, fontSize, width, height);
        if (request is not null) _ = ApplySmallAsync(request);
    }

}
