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
    private static int _skipped;

    private static int _unitPassed;
    private static int _integrationPassed;
    private static int _uiPassed;

    /// <summary>整个进程共享的 WPF Application，窗口构造时靠它解析主题资源。</summary>
    internal static FindFont.App.App? App { get; set; }

    /// <summary>
    /// 只跑某一层；null 表示全跑。CI 上纯逻辑那一层跑在所有平台上，
    /// 依赖桌面会话的用例只在 Windows 上跑。
    /// </summary>
    internal static TestTier? Filter { get; set; }

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
        var tier = TestTiers.Of(name);
        if (Filter is { } only && tier != only)
        {
            _skipped++;
            return;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            body();
            sw.Stop();
            _passed++;
            CountPass(tier);
            Console.WriteLine($"  PASS  [{TestTiers.Describe(tier)}] {name}  ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _failed++;
            Console.WriteLine($"  FAIL  [{TestTiers.Describe(tier)}] {name}  ({sw.ElapsedMilliseconds} ms)");
            Console.WriteLine($"        {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CountPass(TestTier tier)
    {
        switch (tier)
        {
            case TestTier.Unit:
                _unitPassed++;
                break;
            case TestTier.Ui:
                _uiPassed++;
                break;
            default:
                _integrationPassed++;
                break;
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
        if (Filter is { } only)
        {
            Console.WriteLine($"本次只跑了 {TestTiers.Describe(only)} 层，跳过其他 {_skipped} 项。");
        }

        Console.WriteLine(
            $"分层通过：unit {_unitPassed} · integration {_integrationPassed} · ui {_uiPassed}");
        Console.WriteLine($"通过 {_passed} 项，失败 {_failed} 项");
        return _failed == 0 ? 0 : 1;
    }
}
