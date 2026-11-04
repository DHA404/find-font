using System.IO;
using System.Linq;
using FindFont.App.Helpers;
using FindFont.App.Services;
using static FindFont.SmokeTests.TestRunner;

namespace FindFont.SmokeTests;

/// <summary>阶段二加进来的基础设施：单实例、设置、DPI、诊断日志、行数规约。</summary>
internal static class InfrastructureTests
{
    /// <summary>命名互斥体：第一次拿到，第二次就应该判为「已有实例」。</summary>
    internal static void TestSingleInstance()
    {
        using var first = SingleInstance.Acquire();
        Check(first.IsFirstInstance, "第一次 Acquire 应该拿到单实例权");

        using var second = SingleInstance.Acquire();
        Check(!second.IsFirstInstance, "第二次 Acquire 应该识别出已有实例");
    }

    /// <summary>设置对象的默认值、窗口几何捕获、屏幕可见性判断。</summary>
    internal static void TestSettings()
    {
        var settings = new AppSettings();

        Check(settings.WindowWidth > 0, "默认窗口宽度应大于 0");
        Check(settings.PreviewFontSize > 0, "默认字号应大于 0");
        Check(!string.IsNullOrWhiteSpace(settings.PreviewText), "默认样本文字不应为空");

        // 屏幕坐标：0,0 一定在屏内；把点挪到极远处应当判为不可见。
        Check(AppSettings.IsOnSomeScreen(0, 0), "(0,0) 应该在屏幕内");
        Check(!AppSettings.IsOnSomeScreen(-100000, -100000), "极远坐标不应判为可见");

        var roundTrip = new AppSettings
        {
            WindowLeft = 120,
            WindowTop = 90,
            WindowWidth = 1440,
            WindowHeight = 900,
            PreviewText = "Hello 世界",
            PreviewFontSize = 48,
            SelectedCategoryIndex = 2,
            SelectedStyleIndex = 1,
            ShowBrokenOnly = true,
            LastSearchText = "arial",
        };

        Check(roundTrip.WindowWidth == 1440 && roundTrip.PreviewFontSize == 48, "赋值应原样保留");
        Check(roundTrip.LastSearchText == "arial", "上次搜索词应能保存");
    }

    /// <summary>设置从视图模型往返一趟后仍然一致。</summary>
    internal static void TestSettingsRoundTripThroughViewModel()
    {
        var vm = new FindFont.App.ViewModels.MainViewModel();

        var source = new AppSettings
        {
            PreviewText = "RoundTrip 0123",
            PreviewFontSize = 64,
            SelectedCategoryIndex = 1,
            SelectedStyleIndex = 2,
            ShowBrokenOnly = true,
            LastSearchText = "黑体",
        };

        vm.RestoreSettings(source);

        Check(vm.PreviewText == "RoundTrip 0123", "样本文字应被恢复");
        Check(vm.PreviewFontSize == 64, "字号应被恢复");
        Check(vm.SelectedCategoryIndex == 1, "分类应被恢复");
        Check(vm.SelectedStyleIndex == 2, "风格应被恢复");
        Check(vm.ShowBrokenOnly, "只看失效登记应被恢复");
        Check(vm.SearchText == "黑体", "上次搜索词应回到输入框");
        Check(vm.VisibleFonts.Count == 0, "恢复设置不应顺带触发筛选");

        var captured = new AppSettings();
        vm.PersistSettings(captured);
        Check(captured.PreviewText == vm.PreviewText, "写回时样本文字应一致");
        Check(captured.PreviewFontSize == vm.PreviewFontSize, "写回时字号应一致");
        Check(captured.SelectedCategoryIndex == vm.SelectedCategoryIndex, "写回时分类应一致");
        Check(captured.LastSearchText == "黑体", "写回时搜索词应一致");
    }

    /// <summary>DPI 只在合理范围内生效；非法值回落到 96。</summary>
    internal static void TestRenderDpi()
    {
        try
        {
            RenderDpi.Set(144);
            Check(Math.Abs(RenderDpi.Current - 144) < 0.01, "144 DPI 应被接受");

            RenderDpi.Set(0);
            Check(Math.Abs(RenderDpi.Current - RenderDpi.Default) < 0.01, "0 应回落到默认 96");

            RenderDpi.Set(double.NaN);
            Check(Math.Abs(RenderDpi.Current - RenderDpi.Default) < 0.01, "NaN 应回落到默认 96");
        }
        finally
        {
            RenderDpi.Set(RenderDpi.Default);
        }
    }

    /// <summary>同一个请求在 96 与 144 DPI 下必须拿到不同的缓存槽位。</summary>
    internal static void TestDpiParticipatesInCacheKey()
    {
        var cache = new FontRenderCache(16);
        try
        {
            var at96 = new RenderRequest("dpi-probe", null, "DPI 144", 32, 240, 56, 96);
            var at144 = new RenderRequest("dpi-probe", null, "DPI 144", 32, 240, 56, 144);

            var a = cache.RenderAsync(at96).GetAwaiter().GetResult();
            Check(a is not null, "96 DPI 应渲染出位图");

            Check(cache.TryGetFromCache(at96) is not null, "同一 DPI 应命中缓存");
            Check(cache.TryGetFromCache(at144) is null, "144 DPI 不应命中 96 DPI 的缓存");

            var b = cache.RenderAsync(at144).GetAwaiter().GetResult();
            Check(b is not null, "144 DPI 也应渲染出位图");
            Check(cache.Count >= 2, "两个 DPI 应各占一个缓存条目");
        }
        finally
        {
            cache.Dispose();
        }
    }

    /// <summary>日志能落盘，且内容里带上下文。</summary>
    internal static void TestDiagnostics()
    {
        var path = Diagnostics.TodayLogPath;
        Check(path.EndsWith(".log", StringComparison.OrdinalIgnoreCase), "日志路径应以 .log 结尾");

        var marker = "smoke-test-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        Diagnostics.Info(marker);

        Check(File.Exists(path), "写日志后文件应存在");
        var content = File.ReadAllText(path);
        Check(content.Contains(marker, StringComparison.Ordinal), "日志内容应包含刚写入的标记");

        var env = Diagnostics.DescribeEnvironment();
        Check(env.Contains(AppInfo.Version, StringComparison.OrdinalIgnoreCase), "环境摘要应包含版本号");
        Check(env.Contains("OS build", StringComparison.Ordinal), "环境摘要应包含 OS build");
    }

    /// <summary>规约：单个源文件不超过 300 行。</summary>
    internal static void TestSourceFileLineLimit()
    {
        const int limit = 300;
        var offenders = new List<string>();

        foreach (var dir in new[] { "src", "tests" })
        {
            var root = Path.Combine(RootDirectory, dir);
            if (!Directory.Exists(root)) continue;

            foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    && !file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) continue;

                // obj/bin 里的生成文件不归我们管。
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;

                var lines = File.ReadAllLines(file).Length;
                if (lines > limit)
                {
                    offenders.Add($"{Path.GetRelativePath(RootDirectory, file)} = {lines} 行");
                }
            }
        }

        Check(offenders.Count == 0, $"以下文件超过 {limit} 行：{string.Join("；", offenders)}");
    }
}
