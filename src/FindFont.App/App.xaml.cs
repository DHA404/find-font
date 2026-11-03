using System.Windows;
using FindFont.App.Services;
using FindFont.App.Views;

namespace FindFont.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 提权微进程模式：不打开主窗口，执行一次文件/注册表操作后立即退出。
        if (ElevationHost.IsOperationRequest(e.Args))
        {
            Shutdown(ElevationHost.Run(e.Args));
            return;
        }

        var window = new MainWindow();
        MainWindow = window;

        // 在 Show 之前套材质：DWM 只在窗口首次合成时读 backdrop 属性，
        // 显示之后再设会有一次可见的材质闪变。
        Helpers.WindowBackdrop.Apply(window);

        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppServices.RenderCache.Dispose();
        base.OnExit(e);
    }
}
