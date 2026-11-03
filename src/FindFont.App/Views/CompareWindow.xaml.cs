using System.Windows;
using System.Windows.Media.Animation;
using FindFont.App.Helpers;

namespace FindFont.App.Views;

public partial class CompareWindow : Window
{
    public CompareWindow()
    {
        InitializeComponent();

        // 对比窗口是模态的：内容从下方 8px 淡入，让「弹出了一层」这件事有交代。
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        if (!Motion.Enabled) return;

        PaperLayer.Opacity = 0;
        PaperLayer.BeginAnimation(OpacityProperty, Motion.Hold(1, Motion.Base));

        var move = new System.Windows.Media.TranslateTransform(0, 8);
        PaperLayer.RenderTransform = move;
        move.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, Motion.To(0, Motion.Base));
    }
}
