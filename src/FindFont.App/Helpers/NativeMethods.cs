using System.Runtime.InteropServices;

namespace FindFont.App.Helpers;

/// <summary>Win32 互操作声明（字体注册、系统广播、提权检测）。</summary>
internal static class NativeMethods
{
    internal const uint FR_DEFAULT = 0;
    internal const int WM_FONTCHANGE = 0x001D;
    internal const uint SMTO_ABORTIFHUNG = 0x0002;
    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "AddFontResourceExW")]
    internal static extern int AddFontResourceEx(string fileName, uint flags, IntPtr reserved);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RemoveFontResourceExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveFontResourceEx(string fileName, uint flags, IntPtr reserved);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    /// <summary>通知桌面所有窗口字体已变更（安装/卸载字体后必须调用）。</summary>
    internal static void BroadcastFontChange()
    {
        SendMessageTimeout(HWND_BROADCAST, WM_FONTCHANGE, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out _);
    }
}
