using System.Diagnostics;

namespace FindFont.SmokeTests;

/// <summary>
/// 冒烟测试的公共运行器：统计通过/失败、统一输出，并提供共享的 Application 与仓库根目录。
/// 测试用例通过 <c>using static FindFont.SmokeTests.TestRunner;</c> 直接调用 Check。
/// </summary>
internal static class TestRunner
{
    private static int _passed;
    private static int _failed;

    /// <summary>整个进程共享的 WPF Application，窗口构造时靠它解析主题资源。</summary>
    internal static FindFont.App.App? App { get; set; }

    /// <summary>
    /// 仓库根目录。测试在 bin/Debug/net8.0-windows 下运行，
    /// 往上找到含 version.txt 的那一级为止。
    /// </summary>
    internal static readonly string RootDirectory = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "version.txt")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }

    internal static void Run(string name, Action body)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            body();
            sw.Stop();
            _passed++;
            Console.WriteLine($"  PASS  {name}  ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _failed++;
            Console.WriteLine($"  FAIL  {name}  ({sw.ElapsedMilliseconds} ms)");
            Console.WriteLine($"        {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static System.Windows.Application EnsureApp()
    {
        return System.Windows.Application.Current ?? new System.Windows.Application();
    }

    /// <summary>打印分隔线与总计，返回进程退出码。</summary>
    internal static int Summary()
    {
        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"通过 {_passed} 项，失败 {_failed} 项");
        return _failed == 0 ? 0 : 1;
    }
}
