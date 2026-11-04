using System.Diagnostics;
using System.IO;
using System.Text;

namespace FindFont.App.Helpers;

/// <summary>
/// 崩溃与诊断日志。用户报 bug 时唯一能拿到的证据就是这里落盘的东西，
/// 所以每条都带版本号、OS build 和线程信息。
///
/// 目录：%LOCALAPPDATA%\find-font\logs\app-yyyyMMdd.log，只保留最近 7 天。
/// 写日志本身绝不允许再抛异常把进程带走。
/// </summary>
public static class Diagnostics
{
    private const int RetentionDays = 7;

    private static readonly object Gate = new();
    private static bool _pruned;

    private static readonly Lazy<string> ResolvedLogDirectory = new(ResolveLogDirectory, isThreadSafe: true);

    /// <summary>
    /// 日志目录。优先 %LOCALAPPDATA%\find-font\logs；该目录不可写（受限会话、漫游配置损坏）
    /// 时依次回退到 %TEMP%\find-font\logs 和系统临时目录，保证日志真的有地方落。
    /// 结果只探测一次 —— 每条日志都去试建目录太贵。
    /// </summary>
    public static string LogDirectory => ResolvedLogDirectory.Value;

    private static string ResolveLogDirectory()
    {
        var candidates = new List<string>(3);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local)) candidates.Add(Path.Combine(local, "find-font", "logs"));

        var temp = Environment.GetEnvironmentVariable("TEMP") ?? Environment.GetEnvironmentVariable("TMP");
        if (!string.IsNullOrEmpty(temp)) candidates.Add(Path.Combine(temp, "find-font", "logs"));

        var systemTemp = Path.GetTempPath();
        if (!string.IsNullOrEmpty(systemTemp)) candidates.Add(Path.Combine(systemTemp, "find-font", "logs"));

        if (candidates.Count == 0)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(home, ".find-font", "logs"));
        }

        foreach (var candidate in candidates)
        {
            if (IsWritable(candidate)) return candidate;
        }

        // 全都不确定可写时也返回首选路径：Write 自己还有 try/catch 兜底。
        return candidates[0];
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-probe");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>今天的日志文件路径（即使还没写入也返回可用路径，供「打开日志」用）。</summary>
    public static string TodayLogPath => Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>记录一条普通诊断信息。</summary>
    public static void Info(string message) => Write("INFO", message, null);

    /// <summary>记录一条异常。绝不抛出。</summary>
    public static void Log(Exception exception, string? context = null) => Write("ERROR", context ?? exception.GetType().Name, exception);

    /// <summary>
    /// 全局兜底：把未处理异常记下来。返回 true 表示「已经记录，可以按正常崩溃流程走」。
    /// </summary>
    public static bool HandleUnhandled(Exception exception, string source)
    {
        Log(exception, $"unhandled ({source})");
        return true;
    }

    /// <summary>打开日志目录（不存在就先建出来）。返回路径，供 UI 提示。</summary>
    public static string OpenLogFolder()
    {
        var dir = LogDirectory;

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return dir;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // 打不开资源管理器不是错误，路径已经返回给 UI 了。
        }

        return dir;
    }

    /// <summary>环境摘要，用于「关于」与 bug 报告。</summary>
    public static string DescribeEnvironment()
    {
        var sb = new StringBuilder();
        sb.Append("find-font ").AppendLine(AppInfo.Version);
        sb.Append("OS: ").AppendLine(Environment.OSVersion.VersionString);
        sb.Append("OS build: ").AppendLine(Environment.OSVersion.Version.Build.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.Append("Runtime: ").AppendLine(Environment.Version.ToString());
        sb.Append("64-bit process: ").AppendLine(Environment.Is64BitProcess ? "yes" : "no");
        sb.Append("Log dir: ").AppendLine(LogDirectory);
        return sb.ToString();
    }

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            var dir = LogDirectory;
            Directory.CreateDirectory(dir);
            PruneOldFiles(dir);

            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture))
              .Append(" [").Append(level).Append("] ")
              .Append(message);

            if (exception is not null)
            {
                sb.AppendLine();
                sb.Append(exception.GetType().FullName).Append(": ").AppendLine(exception.Message);
                if (!string.IsNullOrEmpty(exception.StackTrace)) sb.AppendLine(exception.StackTrace);
                if (exception.InnerException is not null)
                {
                    sb.Append("  inner: ").Append(exception.InnerException.GetType().FullName)
                      .Append(": ").AppendLine(exception.InnerException.Message);
                }
            }

            sb.AppendLine();

            lock (Gate)
            {
                File.AppendAllText(TodayLogPath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            // 日志写不进去就算了，不能因此让程序崩掉。
        }
    }

    private static void PruneOldFiles(string dir)
    {
        if (_pruned) return;
        _pruned = true;

        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-RetentionDays);
            foreach (var file in Directory.EnumerateFiles(dir, "app-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 清理失败不影响写日志。
        }
    }
}
