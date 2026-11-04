using static FindFont.SmokeTests.TestRunner;

namespace FindFont.SmokeTests;

/// <summary>界面层：资源字典、主窗口构造、动效、材质回退与尺寸。</summary>
internal static class UiTests
{
    /// <summary>
    /// 构造真实窗口，确保 Themes 里的每个 StaticResource 都能解析。
    /// 缺一个画刷或样式，这里就会抛 XamlParseException。
    /// </summary>
    internal static void TestWindowsConstruct()
    {
        var app = TestRunner.App ?? throw new InvalidOperationException("Application 未初始化");
        Check(System.Windows.Application.Current == app, "Application.Current 应指向测试创建的 App");

        Check(app.Resources.Contains("Proof"), "主题里应有 Proof 画刷");
        Check(app.Resources.Contains("SpecimenRow"), "主题里应有 SpecimenRow 行样式");
        Check(app.Resources["Proof"] is System.Windows.Media.SolidColorBrush, "Proof 应是画刷");

        var main = new FindFont.App.Views.MainWindow();
        var mainVm = main.DataContext as FindFont.App.ViewModels.MainViewModel;
        Check(mainVm is not null, "主窗口应自带 MainViewModel");
        Check(main.Title.Contains("find-font"), "主窗口标题应含 find-font");
        Check(main.MinWidth >= 1080, "主窗口最小宽度要能容下三栏");

        var compare = new FindFont.App.Views.CompareWindow();
        Check(compare.DataContext is FindFont.App.ViewModels.CompareViewModel, "对比窗口应自带 CompareViewModel");

        // 索引计数与分类筛选必须自洽：点「中文」后的条数要等于索引上显示的数字。
        if (mainVm is not null)
        {
            mainVm.SeedForStressTest(new List<FontItem>
            {
                new() { FamilyName = "SimSun", ChineseName = "宋体", Category = FontCategory.Chinese },
                new() { FamilyName = "Arial", Category = FontCategory.Latin },
            });
            Check(mainVm.CategoryFacets.Count == 4, "索引应有 4 行（全部/中文/西文/其他）");
            Check(mainVm.CategoryFacets[0].Count == 2, "全部计数应为 2");
            Check(mainVm.CategoryFacets[1].Count == 1, "中文计数应为 1");
            mainVm.SelectedCategoryIndex = 1;
            Check(mainVm.VisibleFonts.Count == mainVm.CategoryFacets[1].Count,
                "点进分类后的条数必须与索引数字一致");
        }

        main.Close();
        compare.Close();
    }

    /// <summary>动效参数必须是「三个时长 + 一条缓动」，且系统关掉动画时整体退化为零时长。</summary>
    internal static void TestMotion()
    {
        Check(FindFont.App.Helpers.Motion.Fast.TimeSpan < FindFont.App.Helpers.Motion.Base.TimeSpan,
            "Fast 应短于 Base");
        Check(FindFont.App.Helpers.Motion.Base.TimeSpan < FindFont.App.Helpers.Motion.Slow.TimeSpan,
            "Base 应短于 Slow");
        Check(FindFont.App.Helpers.Motion.Evaluate(true), "系统允许时应返回 true");
        Check(!FindFont.App.Helpers.Motion.Evaluate(false), "系统禁止时应返回 false");
        Check(FindFont.App.Helpers.Motion.If(false).TimeSpan == TimeSpan.Zero, "不播放时必须是零时长");

        var animation = FindFont.App.Helpers.Motion.To(1, FindFont.App.Helpers.Motion.Fast);
        Check(animation.To is 1d, "To() 应生成目标值正确的动画");
        Check(animation.EasingFunction is not null, "动画应带缓动，避免线性僵硬");
    }

    /// <summary>
    /// 材质是会失败的：老系统、远程桌面、禁用 DWM 都可能拿不到。
    /// 这条测试只保证「失败时安静退回纯色」，绝不因为材质抛异常把启动打挂。
    /// </summary>
    internal static void TestBackdropFallback()
    {
        var window = new FindFont.App.Views.MainWindow();

        var kind = FindFont.App.Helpers.WindowBackdrop.Apply(window);

        if (kind == FindFont.App.Helpers.BackdropKind.None)
        {
            var background = window.Background as System.Windows.Media.SolidColorBrush;
            Check(background is not null && background.Color.A == 255,
                "材质不可用时窗口必须保持不透明底色，不能压出黑底");
        }
        else
        {
            Check(window.Background is not null, "材质生效时也要设一个背景对象");
        }

        // 不支持的系统版本必须走 None，而不是硬试一遍所有 API。
        Check(FindFont.App.Helpers.WindowBackdrop.TryApply(IntPtr.Zero, 7600)
              == FindFont.App.Helpers.BackdropKind.None, "Windows 7 版本号应直接返回 None");

        window.Close();
    }

    /// <summary>画布够大、模板与滚动条资源在位 —— 少一个都会在启动时静默降级成系统默认样式。</summary>
    internal static void TestLayoutAndTemplates()
    {
        var app = TestRunner.App ?? throw new InvalidOperationException("Application 未初始化");

        foreach (var key in new[] { "SpecimenRowTemplate", "SlimScrollBar", "ScrollThumb", "Motion" })
        {
            if (key == "Motion") continue; // Motion 是静态类型，不在资源字典里
            Check(app.Resources.Contains(key), $"资源字典里缺少 {key}");
        }

        Check(app.Resources.Contains("PaperVeil"), "应有材质层用的 PaperVeil 画刷");
        Check(app.Resources.Contains("InkColor"), "配色 token 应同时暴露 Color，供 ColorAnimation 使用");

        var main = new FindFont.App.Views.MainWindow();
        Check(main.Width >= 1600, "主窗口默认宽度应不少于 1600");
        Check(main.Height >= 1000, "主窗口默认高度应不少于 1000");
        Check(main.MinWidth >= 1280, "最小宽度要能容下 272 + 380 两侧栏");
        Check(main.Width / main.MinWidth <= 1.35, "默认画布不应比最小尺寸大太多，避免小屏幕上被迫滚动");

        var compare = new FindFont.App.Views.CompareWindow();
        Check(compare.Width >= 1440, "对比窗口默认宽度应不少于 1440");

        main.Close();
        compare.Close();
    }
}
