using System;
using System.Reflection;

namespace FindFont.App.Helpers;

/// <summary>
/// 版本号的读取入口。真正的值来自仓库根的 version.txt（见 Directory.Build.props），
/// 编译时被写成 <see cref="AssemblyInformationalVersionAttribute"/>，这里只是把它读回来。
/// 界面、Release 说明和打包文件名都用同一处来源，不再各写一份。
/// </summary>
public static class AppInfo
{
    public static string Version
    {
        get
        {
            var raw = typeof(AppInfo).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion
                ?? "0.0.0";

            // SDK 会在后面拼上 +<git sha>（SourceLink），发布文案和文件名里不需要它。
            var plus = raw.IndexOf('+');
            return plus >= 0 ? raw[..plus] : raw;
        }
    }

    /// <summary>例：find-font 0.2.0-alpha</summary>
    public static string DisplayVersion => "find-font " + Version;
}
