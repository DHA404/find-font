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
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppServices.RenderCache.Dispose();
        base.OnExit(e);
    }
}
