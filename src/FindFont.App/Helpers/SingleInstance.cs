using System.Runtime.InteropServices;
using System.Security;
using System.Threading;

namespace FindFont.App.Helpers;

/// <summary>
/// 单实例守卫：用命名互斥体保证同一时刻只有一个主窗口。
///
/// 第二个实例启动时不弹自己的窗口，而是把已存在的那个窗口叫到前台再退出 ——
/// 用户双击两次的预期是「打开那个已经开着的窗口」，不是「再多一个窗口各自扫一遍注册表」。
/// 提权微进程（--install-font 等）不参与单实例，它们不建窗口也不碰互斥体。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Global\find-font-single-instance";

    private const string TitlePrefix = "find-font";
    private const int SwRestore = 9;

    private Mutex? _mutex;

    private SingleInstance(bool isFirstInstance)
    {
        IsFirstInstance = isFirstInstance;
    }

    /// <summary>当前进程是否是第一个实例（false 表示已有实例在跑，调用方应退出）。</summary>
    public bool IsFirstInstance { get; }

    /// <summary>
    /// 尝试成为第一个实例。<paramref name="mutex"/> 在成功时由本对象持有，
    /// 进程异常结束时由系统回收，不会留下僵尸锁。
    /// </summary>
    public static SingleInstance Acquire()
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (createdNew)
            {
                return new SingleInstance(true) { _mutex = mutex };
            }

            mutex.Dispose();
            return new SingleInstance(false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException or WaitHandleCannotBeOpenedException)
        {
            // 拿不到互斥体（权限/异常环境）时不能拒绝启动，宁可多开一个窗口。
            Diagnostics.Log(ex, "single-instance acquire failed");
            return new SingleInstance(true);
        }
    }

    /// <summary>
    /// 把已经在运行的窗口激活到前台。返回是否真的找到了窗口。
    /// </summary>
    public static bool ActivateExistingWindow()
    {
        IntPtr found = IntPtr.Zero;

        try
        {
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;

                var length = GetWindowTextLengthW(hwnd);
                if (length <= 0) return true;

                var buffer = new char[length + 1];
                var copied = GetWindowTextW(hwnd, buffer, buffer.Length);
                if (copied <= 0) return true;

                var title = new string(buffer, 0, copied);
                if (!title.StartsWith(TitlePrefix, StringComparison.OrdinalIgnoreCase)) return true;

                found = hwnd;
                return false; // 停止枚举
            }, IntPtr.Zero);

            if (found == IntPtr.Zero) return false;

            // 最小化的窗口先还原，否则 SetForegroundWindow 只会让它任务栏闪烁。
            if (IsIconic(found)) ShowWindow(found, SwRestore);
            SetForegroundWindow(found);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            Diagnostics.Log(ex, "activate existing window failed");
            return false;
        }
    }

    public void Dispose()
    {
        if (_mutex is null) return;

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 当前线程不持有互斥体（通常是启动时被 Shutdown 打断），忽略。
        }

        _mutex.Dispose();
        _mutex = null;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLengthW(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hwnd, char[] buffer, int maxCount);
}
