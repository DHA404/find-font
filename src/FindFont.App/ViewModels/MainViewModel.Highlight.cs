namespace FindFont.App.ViewModels;

/// <summary>
/// 搜索高亮。命中片段由 <c>FontItem.SetHighlight</c> 切成「前 / 命中 / 后」三段，
/// 视图只负责按段上色；视图模型不碰任何 UI 类型。
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// 只对当前可见结果重算高亮片段。SetHighlight 走 ObservableProperty，
    /// 值没变时不发通知，所以对未变化的行重复调用是廉价的。
    /// </summary>
    private void RefreshHighlights()
    {
        var query = SearchText?.Trim();
        foreach (var item in VisibleFonts) item.SetHighlight(query);
    }
}
