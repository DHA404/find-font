using System.Diagnostics;
using System.Text;
using FindFont.App.Helpers;

namespace FindFont.App.Services;

/// <summary>提权微进程的启动参数协议与执行体。</summary>
public static class ElevationHost
{
    private const string Install = "--install-font";
    private const string Uninstall = "--uninstall-font";
    private const string CleanBroken = "--clean-broken";
    private const string ResultFileArg = "--result";

    /// <summary>判断当前进程是否应以“提权执行体”模式运行。</summary>
    public static bool IsOperationRequest(string[] args) =>
        args is { Length: > 0 } && (args[0] == Install || args[0] == Uninstall || args[0] == CleanBroken);

    /// <summary>提权执行体入口，返回进程退出码。</summary>
    public static int Run(string[] args)
    {
        var mode = args[0];
        var report = new StringBuilder();

        try
        {
            switch (mode)
            {
                case Install:
                {
                    var files = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
                    foreach (var file in files) report.AppendLine(FontInstaller.InstallCore(file));
                    break;
                }
                case Uninstall:
                {
                    var entries = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
                    foreach (var entry in entries) report.AppendLine(FontInstaller.UninstallCore(entry));
                    break;
                }
                case CleanBroken:
                {
                    var entries = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
                    foreach (var entry in entries) report.AppendLine(FontInstaller.RemoveBrokenCore(entry));
                    break;
                }
            }

            NativeMethods.BroadcastFontChange();
            WriteResult(args, report.ToString());
            return 0;
        }
        catch (Exception ex)
        {
            report.AppendLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
            WriteResult(args, report.ToString());
            return 1;
        }
    }

    /// <summary>构造以管理员身份运行本程序的操作参数。</summary>
    internal static string[] BuildArgs(string operation, IEnumerable<string> payload, string? resultFile = null)
    {
        var list = new List<string> { operation };
        list.AddRange(payload);
        if (!string.IsNullOrWhiteSpace(resultFile))
        {
            list.Add("--result");
            list.Add(resultFile!);
        }
        return list.ToArray();
    }

    /// <summary>以 runas 启动自身副本执行提权操作，等待完成并读回结果文本。</summary>
    public static async Task<ElevationResult> RunElevatedAsync(
        string operation,
        IEnumerable<string> payload,
        CancellationToken token = default)
    {
        var payloadList = payload.ToList();
        var resultFile = Path.Combine(Path.GetTempPath(), $"findfont-{Guid.NewGuid():N}.log");

        ProcessStartInfo psi;
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || exePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                // dotnet run / 调试场景：用 dotnet 启动 dll。
                var dll = Path.Combine(AppContext.BaseDirectory, "FindFont.dll");
                psi = new ProcessStartInfo(exePath ?? "dotnet")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                };
                psi.ArgumentList.Add(dll);
            }
            else
            {
                psi = new ProcessStartInfo(exePath!)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                };
            }

            foreach (var arg in BuildArgs(operation, payloadList, resultFile))
                psi.ArgumentList.Add(arg);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return ElevationResult.Fail($"无法创建提权进程: {ex.Message}");
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return ElevationResult.Fail("提权进程启动失败。");

            await process.WaitForExitAsync(token).ConfigureAwait(false);

            var log = File.Exists(resultFile) ? await File.ReadAllTextAsync(resultFile, token).ConfigureAwait(false) : string.Empty;
            TryDelete(resultFile);

            return new ElevationResult(process.ExitCode == 0, process.ExitCode, log);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED：用户在 UAC 弹窗点了“否”。
            return ElevationResult.Cancelled();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return ElevationResult.Fail($"提权执行失败: {ex.Message}");
        }
    }

    private static void WriteResult(string[] args, string content)
    {
        var index = Array.IndexOf(args, ResultFileArg);
        if (index < 0 || index + 1 >= args.Length) return;

        try
        {
            File.WriteAllText(args[index + 1], content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 结果文件写不进去时只能放弃（调用方会看到空日志）。
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>提权操作的返回结果。</summary>
public sealed record ElevationResult(bool Success, int ExitCode, string Log)
{
    public bool WasCancelled { get; init; }

    public static ElevationResult Fail(string message) => new(false, -1, message);

    public static ElevationResult Cancelled() => new(false, 1223, "用户取消了操作。") { WasCancelled = true };
}
