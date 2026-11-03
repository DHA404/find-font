using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace FindFont.Launcher
{
    /// <summary>
    /// .NET Framework 4.8 引导器：真正的客户端程序，只负责检查 .NET 8 桌面运行时并启动 FindFont.App。
    /// <para>
    /// 之所以单独做一个 net48 程序而不是给 App 加自检：Framework 4.8 在所有 Windows 10 1803+
    /// 上都是系统自带组件，用它做入口可以保证「双击一定能弹出对话框」，而不是让用户
    /// 面对一个毫无反应的 exe 或系统级的「找不到运行时」弹窗。
    /// </para>
    /// </summary>
    internal static class Program
    {
        private const string RuntimeConfigName = "FindFont.App.runtimeconfig.json";
        private const string AppExecutableName = "FindFont.App.exe";
        private const string DownloadUrl = "https://dotnet.microsoft.com/download/dotnet/8.0";

        [STAThread]
        private static int Main()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var runtimeConfig = Path.Combine(baseDir, RuntimeConfigName);
            var appExecutable = Path.Combine(baseDir, AppExecutableName);

            if (!File.Exists(appExecutable))
            {
                ShowError("找不到程序主体：\n" + appExecutable + "\n\n请重新解压完整的发布压缩包。");
                return 2;
            }

            var version = ReadRequiredVersion(runtimeConfig);
            if (version != null && !IsRuntimeInstalled(version))
            {
                var answer = MessageBox.Show(
                    "find-font 需要 .NET 8 桌面运行时（Desktop Runtime）。\n\n" +
                    "检测到的缺失版本：" + version + "\n\n" +
                    "是否现在打开下载页面？安装完成后重新运行本程序即可。",
                    "缺少 .NET 8 桌面运行时",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (answer == DialogResult.Yes) OpenDownloadPage();
                return 3;
            }

            return Launch(baseDir, appExecutable);
        }

        private static int Launch(string baseDir, string appExecutable)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = appExecutable,
                WorkingDirectory = baseDir,
                UseShellExecute = true,
            };

            try
            {
                Process.Start(startInfo);
                return 0;
            }
            catch (Exception ex)
            {
                ShowError("启动失败：\n" + ex.Message);
                return 4;
            }
        }

        /// <summary>从 runtimeconfig.json 里取出 tfm 对应的运行时版本，例如 "8.0.0"。</summary>
        private static string ReadRequiredVersion(string runtimeConfigPath)
        {
            try
            {
                if (!File.Exists(runtimeConfigPath)) return "8.0.0";
                var json = File.ReadAllText(runtimeConfigPath);

                var marker = "\"version\"";
                var index = json.IndexOf(marker, StringComparison.Ordinal);
                if (index < 0) return "8.0.0";

                var colon = json.IndexOf(':', index);
                var firstQuote = json.IndexOf('"', colon + 1);
                var secondQuote = json.IndexOf('"', firstQuote + 1);
                if (firstQuote < 0 || secondQuote < 0) return "8.0.0";

                return json.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
            }
            catch (Exception)
            {
                return "8.0.0";
            }
        }

        /// <summary>用 dotnet --list-runtimes 检测 Microsoft.WindowsDesktop.App 是否满足版本要求。</summary>
        private static bool IsRuntimeInstalled(string requiredVersion)
        {
            if (!Version.TryParse(Normalize(requiredVersion), out var required)) return true;

            var output = RunDotnetListRuntimes();
            if (output == null) return true; // 检测不了就不要拦着用户。

            foreach (var line in output.Split('\n'))
            {
                var text = line.Trim();
                if (text.IndexOf("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                foreach (var token in text.Split(' '))
                {
                    if (!Version.TryParse(Normalize(token), out var installed)) continue;
                    if (installed >= required) return true;
                }
            }

            return false;
        }

        private static string RunDotnetListRuntimes()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "--list-runtimes",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return null;
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000);
                    return output;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Normalize(string version)
        {
            var text = version.Trim().Trim('"');
            var dash = text.IndexOf('-');
            if (dash >= 0) text = text.Substring(0, dash);

            var parts = text.Split('.');
            if (parts.Length == 1) return text + ".0.0";
            if (parts.Length == 2) return text + ".0";
            return text;
        }

        private static void OpenDownloadPage()
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = DownloadUrl, UseShellExecute = true });
            }
            catch (Exception)
            {
                // 打不开浏览器也不阻塞退出。
            }
        }

        private static void ShowError(string message)
            => MessageBox.Show(message, "find-font", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
}
