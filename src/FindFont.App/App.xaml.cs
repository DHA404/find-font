using System.Windows;
using System.Windows.Threading;
using FindFont.App.Helpers;
using FindFont.App.Services;
using FindFont.App.Views;

namespace FindFont.App;

public partial class App : Application
{
    private SingleInstance? _instance;

    /// <summary>
    /// 只给冒烟测试用：Application 构造函数会把 OnStartup 投递成一条 dispatcher 操作，
    /// 测试里任何一次 Dispatcher.Invoke 都会让它迟到执行，从而抢走单实例互斥体、
    /// 顺带创建出一个没人要的主窗口。测试在构造 App 之前把它置 true。
    /// </summary>
    internal static bool StartupSuppressed { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (StartupSuppressed) return;

        StartupTrace.Mark("app-startup");
        base.OnStartup(e);

        // 提权微进程模式：不打开主窗口，执行一次文件/注册表操作后立即退出。
        // 它不参与单实例，也不写设置 —— 它连窗口都没有。
        if (ElevationHost.IsOperationRequest(e.Args))
        {
            Shutdown(ElevationHost.Run(e.Args));
            return;
        }

        InstallCrashHandlers();
        StartupTrace.Mark("crash-handlers");

        // 单实例：第二个实例把已有窗口叫到前台然后自己退出。
        _instance = SingleInstance.Acquire();
        if (!_instance.IsFirstInstance)
        {
            var activated = SingleInstance.ActivateExistingWindow();
            if (!activated)
            {
                Diagnostics.Info("second instance could not find an existing window to activate");
            }

            Shutdown(0);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;

        // DPI 必须在任何渲染请求之前就位。窗口一构造出来句柄还没创建，
        // 但 VisualTreeHelper 仍能拿到所属显示器的 DPI；等到 Loaded 才设的话，
        // 首屏那批请求会全按 96 DPI 出图再被系统拉伸。
        Services.RenderDpi.Track(window);
        StartupTrace.Mark("window-created");

        // 窗口几何/内容从设置恢复，必须在 Show 之前，否则会看到窗口从默认位置跳一下。
        AppSettings.Current.ApplyWindow(window);

        // 在 Show 之前套材质：DWM 只在窗口首次合成时读 backdrop 属性，
        // 显示之后再设会有一次可见的材质闪变。
        WindowBackdrop.Apply(window);

        window.Show();
        StartupTrace.Mark("window-shown");
        Diagnostics.Info(StartupTrace.Describe());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 先落盘设置，再拆渲染线程；顺序反了会让正在写的日志撞上已释放的 Dispatcher。
        try
        {
            if (MainWindow is { } window) AppSettings.Current.CaptureWindow(window);
            AppSettings.Current.Save();
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidCastException)
        {
            Diagnostics.Log(ex, "settings save on exit failed");
        }

        AppServices.RenderCache.Dispose();
        _instance?.Dispose();
        _instance = null;

        base.OnExit(e);
    }

    /// <summary>
    /// 把两个全局异常口子都堵上：UI 线程走 DispatcherUnhandledException，
    /// 后台线程（渲染线程、Task.Run）走 AppDomain.UnhandledException。
    /// 记录完之后仍然按正常崩溃流程走 —— 我们只负责留证据，不假装能恢复。
    /// </summary>
    private void InstallCrashHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Diagnostics.HandleUnhandled(e.Exception, "ui-thread");

        // 设置与日志在崩溃前尽量保住，否则用户重启后丢失现场。
        try
        {
            if (MainWindow is { } window) AppSettings.Current.CaptureWindow(window);
            AppSettings.Current.Save();
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidCastException)
        {
            Diagnostics.Log(ex, "settings save during crash failed");
        }

        // 不设 e.Handled：让程序照常退出，只是多了一份日志。
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Diagnostics.HandleUnhandled(exception, "background");
        }
        else
        {
            Diagnostics.Info($"unhandled non-exception: {e.ExceptionObject}");
        }
    }
}
