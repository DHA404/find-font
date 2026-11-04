using FindFont.App.Services;

namespace FindFont.App.ViewModels;

/// <summary>
/// 设置的读写与 DPI 变化处理。
///
/// 拆成 partial 而不是塞回主文件：主文件已经很长，而这块边角逻辑只跟
/// 窗口生命周期打交道，与筛选/渲染的主链路没有耦合。
/// </summary>
public partial class MainViewModel
{
    /// <summary>恢复设置期间压住搜索防抖，避免一启动就自动筛选。</summary>
    private bool _suspendSearch;

    /// <summary>从设置恢复用户上次的界面状态。必须在加载字体之前调用。</summary>
    public void RestoreSettings(AppSettings settings)
    {
        if (settings is null) return;

        if (!string.IsNullOrWhiteSpace(settings.PreviewText)) PreviewText = settings.PreviewText;
        if (settings.PreviewFontSize > 0) PreviewFontSize = settings.PreviewFontSize;

        var category = settings.SelectedCategoryIndex;
        if (category >= 0 && category < CategoryOptions.Count) SelectedCategoryIndex = category;

        var style = settings.SelectedStyleIndex;
        if (style >= 0 && style < StyleOptions.Count) SelectedStyleIndex = style;

        ShowBrokenOnly = settings.ShowBrokenOnly;

        // 上次的搜索词不自动恢复成筛选条件 —— 打开就只看 24 个字体容易让人以为字体丢了。
        // 但把它留在输入框里，用户敲一下回车或点一下搜索框就能继续上次的活。
        if (!string.IsNullOrWhiteSpace(settings.LastSearchText))
        {
            // 挂起防抖再写属性：走正常 setter 会立刻开始筛选，
            // 而我们只想把词放回输入框、等用户自己按回车。
            _suspendSearch = true;
            try
            {
                SearchText = settings.LastSearchText!;
            }
            finally
            {
                _suspendSearch = false;
            }
        }
    }

    /// <summary>把当前界面状态写回设置对象（由窗口在关闭时调用）。</summary>
    public void PersistSettings(AppSettings settings)
    {
        if (settings is null) return;

        settings.PreviewText = PreviewText;
        settings.PreviewFontSize = PreviewFontSize;
        settings.SelectedCategoryIndex = SelectedCategoryIndex;
        settings.SelectedStyleIndex = SelectedStyleIndex;
        settings.ShowBrokenOnly = ShowBrokenOnly;
        settings.LastSearchText = SearchText;
    }

    /// <summary>
    /// DPI 变了：位图按旧 DPI 出的是模糊的，必须全部作废重出。
    /// 缓存键里含 DPI，所以清掉缓存后新请求自然会按新 DPI 渲染。
    /// </summary>
    public void RefreshDpi()
    {
        AppServices.RenderCache.Clear();
        RefreshPreviewBitmaps();
        RequestLargePreviews(_lastLargeSelected);
    }

    /// <summary>打开日志目录。用户报 bug 时第一个该点的按钮。</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void OpenLogFolder()
    {
        var dir = Helpers.Diagnostics.OpenLogFolder();
        StatusText = $"日志目录：{dir}";
    }

    /// <summary>「关于」：把版本与环境信息放进可复制的文本框，方便贴进 issue。</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ShowAbout()
    {
        var text = Helpers.Diagnostics.DescribeEnvironment()
            + $"字体总数：{TotalCount}\n"
            + $"失效登记：{BrokenCount}\n"
            + $"当前筛选：{CategoryOptions[SelectedCategoryIndex]} / {StyleOptions[SelectedStyleIndex]}\n"
            + $"渲染 DPI：{AppServices.RenderCache.ByteBudget / (1024 * 1024)} MB 预算，"
            + $"已渲染 {AppServices.RenderCache.RenderedFrames} 帧";

        var window = new Views.AboutWindow(text) { Owner = System.Windows.Application.Current.MainWindow };
        window.ShowDialog();
    }
}
