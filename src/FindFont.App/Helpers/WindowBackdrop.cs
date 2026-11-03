using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FindFont.App.Helpers;

/// <summary>窗口背景材质。None 表示系统不支持，界面退回纯色。</summary>
public enum BackdropKind
{
    None = 0,
    Mica = 1,
    Acrylic = 2,
}

/// <summary>
/// 让窗口背景走 DWM 材质（Mica / Acrylic）。
/// 三级回退：Win11 22H2 的 SYSTEMBACKDROP_TYPE → Win11 21H2 的 MICA_EFFECT → Win10 1803 的 ACCENT 亚克力。
/// 全部失败时返回 <see cref="BackdropKind.None"/>，调用方保持不透明底色 —— 绝不出现黑底或半透明压字。
/// </summary>
internal static class WindowBackdrop
{
    private const int DwmwaSystemBackdropType = 38; // Win11 22H2+
    private const int DwmwaMicaEffect = 1029;       // Win11 21H2

    private const int DwmSystemBackdropMica = 2;
    private const int DwmSystemBackdropAcrylic = 3;

    private const int AccentEnableAcrylicBlurBehind = 4; // Win10 1803+
    private const int WcaAccentPolicy = 19;

    /// <summary>给窗口套上材质；成功时把背景切成透明，让 DWM 材质透出来。</summary>
    public static BackdropKind Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            if (hwnd == IntPtr.Zero) return BackdropKind.None;

            if (!window.AllowsTransparency) ExtendFrameToClientArea(hwnd);

            var kind = TryApply(hwnd, Environment.OSVersion.Version.Build);
            if (kind != BackdropKind.None) ApplyTransparentChrome(window);
            return kind;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException or InvalidOperationException)
        {
            return BackdropKind.None;
        }
    }

    /// <summary>按系统版本挑选可用的材质 API。抽出来是为了能测回退顺序而不依赖真实系统。</summary>
    internal static BackdropKind TryApply(IntPtr hwnd, int build)
    {
        if (build >= 22621 && TrySystemBackdrop(hwnd, DwmSystemBackdropMica)) return BackdropKind.Mica;
        if (build >= 22621 && TrySystemBackdrop(hwnd, DwmSystemBackdropAcrylic)) return BackdropKind.Acrylic;
        if (build >= 22000 && TryMicaEffect(hwnd)) return BackdropKind.Mica;
        if (build >= 17763 && TryAcrylicAccent(hwnd)) return BackdropKind.Acrylic;
        return BackdropKind.None;
    }

    private static bool TrySystemBackdrop(IntPtr hwnd, int type)
    {
        try
        {
            var value = type;
            return DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref value, sizeof(int)) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryMicaEffect(IntPtr hwnd)
    {
        try
        {
            var value = 1;
            return DwmSetWindowAttribute(hwnd, DwmwaMicaEffect, ref value, sizeof(int)) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryAcrylicAccent(IntPtr hwnd)
    {
        // Win10 的亚克力：GradientColor 里带一点灰，避免纯白底洗掉文字。
        var accent = new AccentPolicy
        {
            AccentState = AccentEnableAcrylicBlurBehind,
            AccentFlags = 2,
            GradientColor = unchecked((int)0xCCECEDEF),
        };

        var size = Marshal.SizeOf<AccentPolicy>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, buffer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = buffer,
                SizeOfData = size,
            };
            return SetWindowCompositionAttribute(hwnd, ref data) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ExtendFrameToClientArea(IntPtr hwnd)
    {
        try
        {
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
        }
        catch (DllNotFoundException)
        {
            // 没有 dwmapi 的系统本来也没材质可用，忽略。
        }
    }

    private static void ApplyTransparentChrome(Window window)
    {
        // 窗口本身透明，材质由 DWM 画；内容层铺一层半透明纸色，保证正文永远有足够对比度。
        window.Background = System.Windows.Media.Brushes.Transparent;
        if (window.FindName("PaperLayer") is System.Windows.Controls.Panel panel
            && window.TryFindResource("PaperVeil") is System.Windows.Media.Brush veil)
        {
            panel.Background = veil;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
}
