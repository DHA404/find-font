using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using FindFont.App.Models;

namespace FindFont.App.Views;

/// <summary>
/// 瓦片网格的命中测试与布局度量。ListBox 只承载「行」这一级，
/// 点击落在哪一列必须从 OriginalSource 上溯才能知道，所以单独放在这里。
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// 点击瓦片：Ctrl 切换单个、Shift 从锚点连选、否则独占。
    /// 从 OriginalSource 往上找到第一个带 FontItem 数据上下文的元素 —— 瓦片内部
    /// 点到的可能是 TextBlock、Image 或 Border，但它们的 DataContext 都是同一个字体。
    /// </summary>
    private void OnTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindFontItem(e.OriginalSource) is not { } item) return;

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        ViewModel.ToggleTileSelection(item, ctrl, shift);
        e.Handled = true;
    }

    /// <summary>从命中的可视元素上溯，找出它属于哪个字体瓦片。</summary>
    private static FontItem? FindFontItem(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is FrameworkElement element && element.DataContext is FontItem item) return item;
            current = current is Visual or Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>
    /// 窗口尺寸变化时重算列数与瓦片宽度。右栏展开/收起也会改变列表可用宽度，
    /// 所以 IsDetailVisible 变化时再补一次。
    /// </summary>
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged || e.HeightChanged) RefreshTileMetrics();
    }

    private void RefreshTileMetrics()
    {
        var listWidth = FontRowList.ActualWidth > 0
            ? FontRowList.ActualWidth
            : Math.Max(0, ActualWidth - 208);
        ViewModel.UpdateTileMetrics(ActualWidth, listWidth);
    }

    /// <summary>Ctrl+A 选中全部可见字体；列表本来就是多选语义，只是入口在瓦片上。</summary>
    private void SelectAllVisible()
    {
        ViewModel.SelectAllVisible();
    }

}
