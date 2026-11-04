using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FindFont.App.Views;

/// <summary>
/// 「关于」小窗：把版本与环境信息展示成可复制的文本。
/// 用户报 bug 时不用再让他去找日志目录、抄 OS build。
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow(string detail)
    {
        InitializeComponent();
        DetailBox.Text = detail;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DetailBox.Text);
            StatusLine.Text = "已复制到剪贴板。";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // 剪贴板被别的进程锁住时会抛，提示用户手动选中复制。
            StatusLine.Text = "剪贴板被占用，请手动选中上面的文字复制。";
        }
    }

    private void OnOpenLogClick(object sender, RoutedEventArgs e)
    {
        StatusLine.Text = Helpers.Diagnostics.OpenLogFolder();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
