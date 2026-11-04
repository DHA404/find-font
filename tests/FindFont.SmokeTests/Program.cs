using System.Diagnostics;
using System.Windows;
using FindFont.App.Helpers;
using FindFont.App.Models;
using FindFont.App.Services;

namespace FindFont.SmokeTests;

/// <summary>无第三方依赖的冒烟测试：直接跑真实字体库，失败时返回非零退出码。</summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static FindFont.App.App? _app;

    /// <summary>
    /// 仓库根目录。测试在 bin/Debug/net8.0-windows 下运行，
    /// 往上找到含 version.txt 的那一级为止。
    /// </summary>
    private static readonly string RootDirectory = FindRepoRoot();

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

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("find-font 冒烟测试");
        Console.WriteLine(new string('-', 60));

        // WPF 一个进程只能有一个 Application；先建好，窗口构造时才能解析主题资源。
        _app = new FindFont.App.App();
        _app.InitializeComponent();
        // 测试里会反复开关窗口；默认的 OnLastWindowClose 会在最后一个窗口关闭时
        // 触发 Application.OnExit 并释放全局渲染缓存，后面的用例就全拿不到位图了。
        _app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Run("LanguageInference 关键字推断", TestLanguageInference);
        Run("FontMetadataReader CJK 判定", TestContainsCjk);
        Run("FontItem.BestName 优先级", TestBestName);
        Run("FontScanner 扫描系统字体", TestScanner);
        Run("FontScanner.EnhanceAsync 补全名称", TestEnhance);
        Run("FontMetadataReader 直读 sfnt 全量解析", TestMetadataReaderAll);
        Run("FontRenderCache 离屏渲染出位图", TestRenderCache);
        Run("MainViewModel 搜索与筛选", TestViewModelFilter);
        Run("10000 条字体的搜索与筛选压测", TestStressFilter);
        Run("CompareViewModel 最多三栏", TestCompareViewModel);
        Run("界面资源与主窗口可构造", TestWindowsConstruct);
        Run("动效助手遵循系统设置", TestMotion);
        Run("窗口材质回退不抛异常", TestBackdropFallback);
        Run("尺寸与模板资源", TestLayoutAndTemplates);
        Run("版本号来自 version.txt", TestVersionWiring);
        Run("位图缓存受字节预算约束", TestCacheByteBudget);
        Run("同键并发请求只渲染一次", TestCacheDedupesInFlight);
        Run("未选中的行不渲染大图", TestNoLargePreviewWhenIdle);
        Run("RangeObservableCollection 只发一次 Reset", TestRangeCollection);
        Run("搜索快路径与原多字段匹配等价", TestSearchParity);
        Run("卸载不删系统字体文件", TestUninstallProtectsSystemFonts);
        Run("网页字体（WOFF）明确拒装", TestWebFontsRejected);
        Run("字体注册表值名格式正确", TestRegistryValueName);

        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"通过 {_passed} 项，失败 {_failed} 项");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action body)
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

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void TestLanguageInference()
    {
        Check(LanguageInference.Infer("SimSun") == FontLanguage.Chinese, "SimSun 应为中文");
        Check(LanguageInference.Infer("Microsoft YaHei") == FontLanguage.Chinese, "微软雅黑应为中文");
        Check(LanguageInference.Infer("Meiryo") == FontLanguage.Japanese, "Meiryo 应为日文");
        Check(LanguageInference.Infer("Malgun Gothic") == FontLanguage.Korean, "Malgun 应为韩文");
        Check(LanguageInference.Infer("Segoe UI") == FontLanguage.Latin, "Segoe UI 应为西文");
        Check(LanguageInference.Infer(null, "") == FontLanguage.Unknown, "空名应为 Unknown");
        Check(LanguageInference.ToCategory(FontLanguage.Chinese) == FontCategory.Chinese, "中文分类映射");
        Check(LanguageInference.FromCategory(FontCategory.Latin) == FontLanguage.Latin, "西文反查映射");
    }

    private static void TestContainsCjk()
    {
        Check(FontMetadataReader.ContainsCjk("微软雅黑"), "应识别中文");
        Check(FontMetadataReader.ContainsCjk("游ゴシック"), "应识别日文汉字");
        Check(!FontMetadataReader.ContainsCjk("Segoe UI"), "纯西文不应判为 CJK");
        Check(!FontMetadataReader.ContainsCjk(null), "null 不应判为 CJK");
    }

    private static void TestBestName()
    {
        var item = new FontItem { DisplayName = "SimSun", LocalizedName = "宋体", ChineseName = "宋体" };
        Check(item.BestName == "宋体", $"BestName 应为宋体，实际 {item.BestName}");
        item.ChineseName = string.Empty;
        Check(item.BestName == "宋体", "回退到 LocalizedName");
        item.LocalizedName = string.Empty;
        Check(item.BestName == "SimSun", "回退到 DisplayName");

        var a = new FontItem { FamilyName = "Arial", StyleName = "Regular" };
        var b = new FontItem { FamilyName = "arial", StyleName = "regular" };
        Check(a.DedupKey == b.DedupKey, "DedupKey 应忽略大小写");
    }

    private static void TestScanner()
    {
        var scanner = new FontScanner();
        var result = scanner.Scan();
        Console.WriteLine($"        注册表项 {result.RegistryEntries}，字体 {result.Items.Count}，" +
                          $"缺失文件 {result.MissingFiles}，耗时 {result.Elapsed.TotalMilliseconds:F0} ms");
        Check(result.Items.Count > 0, "系统应当至少扫描到 1 个字体");
        Check(result.Items.All(i => !string.IsNullOrWhiteSpace(i.FileName)), "每项都应有文件名");
        Check(result.Items.All(i => !string.IsNullOrWhiteSpace(i.DisplayName)), "每项都应有显示名");

        // 同一个字体文件不能在列表里出现两次。注册表的 64/32 视图各有一份同名登记，
        // 用户字体目录又与注册表指向同一批文件，若按「家族名+样式名」去重会残留重复。
        var samePath = result.Items
            .Where(i => i.FullPath.Length > 0)
            .GroupBy(i => i.FullPath, StringComparer.OrdinalIgnoreCase)
            .Count(g => g.Count() > 1);
        Check(samePath == 0, $"同一文件路径不应出现多次，实际 {samePath} 处重复");
    }

    private static void TestEnhance()
    {
        var scanner = new FontScanner();
        var result = scanner.Scan();
        var sample = result.Items.Take(12).ToList();

        scanner.EnhanceAsync(sample, null, null, CancellationToken.None).GetAwaiter().GetResult();

        var enriched = sample.Count(i => !string.IsNullOrWhiteSpace(i.LocalizedName) || i.Weight != 400);
        Console.WriteLine($"        抽样 {sample.Count} 项，{enriched} 项补全了本地化名或字重");
        Check(sample.Any(i => i.Weight > 0), "字重应被回填");

        // 旧式 .fon 解析不出家族名很常见，但不能被标成异常字体。
        var legacy = result.Items
            .Where(i => i.FullPath.EndsWith(".fon", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (legacy.Count > 0)
        {
            scanner.EnhanceAsync(legacy, null, null, CancellationToken.None).GetAwaiter().GetResult();
            Check(legacy.All(i => !i.IsBroken), "旧式 .fon 字体不应被判定为异常");
            Console.WriteLine($"        旧式 .fon {legacy.Count} 项均未误判为异常");
        }
    }

    private static void TestMetadataReaderAll()
    {
        var result = new FontScanner().Scan();
        var files = result.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.FullPath) && File.Exists(i.FullPath))
            .GroupBy(i => i.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        Check(files.Count > 0, "应当有可读取的字体文件");

        var sw = Stopwatch.StartNew();
        var parsed = 0;
        var failed = new List<string>();
        foreach (var item in files)
        {
            if (FontMetadataReader.TryRead(item.FullPath) is null) failed.Add(Path.GetFileName(item.FullPath));
            else parsed++;
        }
        sw.Stop();

        var perFont = sw.Elapsed.TotalMilliseconds / files.Count;
        Console.WriteLine($"        {files.Count} 个文件，解析成功 {parsed}，" +
                          $"{sw.ElapsedMilliseconds} ms（{perFont:F2} ms/文件）");
        if (failed.Count > 0)
            Console.WriteLine($"        未解析：{string.Join(", ", failed.Take(8))}");

        // 旧式 .fon 位图字体不是 sfnt 容器，按设计走 GlyphTypeface 回退并可能失败。
        var sfntFiles = files.Where(i => !i.FullPath.EndsWith(".fon", StringComparison.OrdinalIgnoreCase)).ToList();
        var sfntFailed = sfntFiles.Count(i => FontMetadataReader.TryRead(i.FullPath) is null);
        Check(sfntFailed == 0, $"sfnt 字体不应解析失败，实际 {sfntFailed} 个");

        // 关键断言：直读路径必须能独立解析出所有 sfnt 字体。若这里失败，
        // 说明 TryRead 退回了几十毫秒一个的 GlyphTypeface 回退路径。
        // （不在这里卡耗时：CI 的磁盘随机读可能比本机慢一个数量级，用毫秒数做断言只会假失败。）
        var directFailed = sfntFiles.Count(i => FontMetadataReader.TryReadSfnt(i.FullPath) is null);
        Check(directFailed == 0, $"直读 sfnt 路径不应退回 GlyphTypeface，实际 {directFailed} 个文件只能靠回退");
        Console.WriteLine($"        直读覆盖 {sfntFiles.Count}/{sfntFiles.Count}，平均 {perFont:F2} ms/文件");

        // 抽样验证解析出的名字与实际字体一致（本机存在时才断言）。
        AssertFamily(files, "simsun.ttc", "SimSun", "宋体");
        AssertFamily(files, "arial.ttf", "Arial", null);
        AssertFamily(files, "msyh.ttc", "Microsoft YaHei", "微软雅黑");
    }

    private static void AssertFamily(List<FontItem> files, string fileName, string family, string? chinese)
    {
        var item = files.FirstOrDefault(f =>
            string.Equals(Path.GetFileName(f.FullPath), fileName, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;

        var info = FontMetadataReader.TryRead(item.FullPath);
        Check(info is not null, $"{fileName} 应能解析");
        if (info is null) return;
        Check(string.Equals(info.FamilyName, family, StringComparison.OrdinalIgnoreCase),
            $"{fileName} 家族名应为 {family}，实际 {info.FamilyName}");
        if (chinese is not null)
            Check(string.Equals(info.ChineseFamilyName, chinese, StringComparison.OrdinalIgnoreCase),
                $"{fileName} 中文名应为 {chinese}，实际 {info.ChineseFamilyName}");
        Check(info.Weight > 0, $"{fileName} 字重应被读到");
    }

    private static void TestStressFilter()
    {
        EnsureApp();
        const int count = 10000;
        var items = new List<FontItem>(count);
        for (var i = 0; i < count; i++)
        {
            var isChinese = i % 3 == 0;
            items.Add(new FontItem
            {
                FamilyName = $"Stress Family {i}",
                DisplayName = isChinese ? $"压测字体 {i}" : $"Stress Font {i}",
                LocalizedName = $"Stress Family {i}",
                FileName = $"stress_{i}.ttf",
                StyleName = i % 4 == 0 ? "Bold" : "Regular",
                Weight = i % 4 == 0 ? 700 : 400,
                Category = isChinese ? FontCategory.Chinese : FontCategory.Latin,
            });
        }

        var vm = new FindFont.App.ViewModels.MainViewModel();
        var seed = Stopwatch.StartNew();
        vm.SeedForStressTest(items);
        seed.Stop();
        Check(vm.VisibleFonts.Count == count, $"无筛选时应可见全部 {count} 项");

        var sw = Stopwatch.StartNew();
        vm.SearchText = "stress family 42";
        sw.Stop();
        Check(vm.VisibleFonts.Count > 0, "压测搜索应有命中");
        var searchMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        vm.SelectedCategoryIndex = 1;
        sw.Stop();
        var chineseMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        vm.SelectedCategoryIndex = 0;
        vm.SearchText = string.Empty;
        sw.Stop();
        var resetMs = sw.Elapsed.TotalMilliseconds;

        Console.WriteLine($"        装载 {seed.Elapsed.TotalMilliseconds:F0} ms，" +
                          $"搜索 {searchMs:F0} ms，中文筛选 {chineseMs:F0} ms，恢复 {resetMs:F0} ms");

        Check(searchMs < 500, $"1 万条搜索耗时应 < 500 ms，实际 {searchMs:F0} ms");
        Check(chineseMs < 500, $"1 万条筛选耗时应 < 500 ms，实际 {chineseMs:F0} ms");
        Check(vm.VisibleFonts.Count == count, "清空条件后应恢复全量");
    }

    private static void TestCompareViewModel()
    {
        var fonts = Enumerable.Range(1, 5)
            .Select(i => new FontItem { FamilyName = $"Font {i}", DisplayName = $"字体 {i}", StyleName = "Regular" })
            .ToList();

        var vm = new FindFont.App.ViewModels.CompareViewModel();
        vm.Load(fonts);
        Check(vm.ColumnCount == FindFont.App.ViewModels.CompareViewModel.MaxColumns,
            $"对比窗口最多 {FindFont.App.ViewModels.CompareViewModel.MaxColumns} 栏，实际 {vm.ColumnCount}");
        Check(!vm.IsEmpty, "加载后不应为空");

        vm.RemoveCommand.Execute(vm.Items[0]);
        Check(vm.ColumnCount == 2, "移除一栏后应剩 2 栏");

        vm.RemoveCommand.Execute(vm.Items[0]);
        vm.RemoveCommand.Execute(vm.Items[0]);
        Check(vm.IsEmpty, "移除全部后应为空");
    }

    private static System.Windows.Application EnsureApp()
    {
        return System.Windows.Application.Current ?? new System.Windows.Application();
    }

    private static void TestViewModelFilter()
    {
        EnsureApp();
        var vm = new FindFont.App.ViewModels.MainViewModel();
        vm.LoadAsync().GetAwaiter().GetResult();

        var total = vm.VisibleFonts.Count;
        Console.WriteLine($"        无筛选可见 {total} 项");
        Check(total > 0, "加载后应有可见字体");

        vm.SelectedCategoryIndex = 1; // 中文
        Check(vm.VisibleFonts.All(f => f.Category == FontCategory.Chinese), "中文筛选应只留中文类");
        var chinese = vm.VisibleFonts.Count;
        Console.WriteLine($"        中文分类 {chinese} 项");
        Check(chinese > 0 && chinese < total, "中文类应为非空真子集");

        vm.SelectedCategoryIndex = 0;
        vm.SearchText = "arial";
        Check(vm.VisibleFonts.Count > 0, "搜索 arial 应有结果");
        Check(vm.VisibleFonts.All(f =>
                f.DisplayName.Contains("arial", StringComparison.OrdinalIgnoreCase)
                || f.LocalizedName.Contains("arial", StringComparison.OrdinalIgnoreCase)
                || f.ChineseName.Contains("arial", StringComparison.OrdinalIgnoreCase)
                || f.FamilyName.Contains("arial", StringComparison.OrdinalIgnoreCase)
                || f.FileName.Contains("arial", StringComparison.OrdinalIgnoreCase)
                || f.StyleName.Contains("arial", StringComparison.OrdinalIgnoreCase)),
            "搜索结果必须全部命中断词");
        Console.WriteLine($"        搜索 arial 命中 {vm.VisibleFonts.Count} 项");

        vm.SearchText = "zzzz-不存在的字体";
        Check(vm.VisibleFonts.Count == 0, "无意义查询应返回空集");

        vm.SearchText = string.Empty;
        Check(vm.VisibleFonts.Count == total, "清空搜索应恢复全量");

        vm.SelectedStyleIndex = 2; // 粗体
        Check(vm.VisibleFonts.All(f => f.IsBold), "粗体筛选应只留粗体");
        vm.SelectedStyleIndex = 0;
    }

    private static void TestRenderCache()
    {
        using var cache = new FontRenderCache(capacity: 16);
        var item = new FontItem { FamilyName = "Segoe UI", FullPath = string.Empty };
        var request = new RenderRequest(item.DedupKey, null, "Hello 你好 0123", 32, 480, 56);
        var result = cache.RenderAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        Check(result is not null, "渲染结果不应为 null");
        if (result is null) return;
        var bitmap = result;
        Check(bitmap.Width > 0 && bitmap.Height > 0, "位图尺寸应大于 0");
        Check(bitmap.IsFrozen, "跨线程使用的位图必须 Freeze");

        var again = cache.TryGetFromCache(request);
        Check(again is not null, "第二次请求应命中缓存");
    }

    /// <summary>
    /// 构造真实窗口，确保 Themes 里的每个 StaticResource 都能解析。
    /// 缺一个画刷或样式，这里就会抛 XamlParseException。
    /// </summary>
    private static void TestWindowsConstruct()
    {
        var app = _app ?? throw new InvalidOperationException("Application 未初始化");
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

    private static void TestVersionWiring()
    {
        var expected = File.ReadAllText(Path.Combine(RootDirectory, "version.txt")).Trim();
        Check(expected.Length > 0, "version.txt 不能为空");

        var actual = FindFont.App.Helpers.AppInfo.Version;
        Check(actual == expected, $"程序集版本 {actual} 应与 version.txt 的 {expected} 一致");

        Check(FindFont.App.Helpers.AppInfo.DisplayVersion.EndsWith(expected),
            "DisplayVersion 应包含 version.txt 里的版本号");

        var vm = new FindFont.App.ViewModels.MainViewModel();
        Check(vm.AppVersion == expected, "主视图模型应暴露同一个版本号");

        Check(vm.AppVersion.Split('.').Length >= 3, "版本号至少三段，便于 tag 与 zip 命名对齐");
    }

    /// <summary>动效参数必须是「三个时长 + 一条缓动」，且系统关掉动画时整体退化为零时长。</summary>
    private static void TestMotion()
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
    private static void TestBackdropFallback()
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
    private static void TestLayoutAndTemplates()
    {
        var app = _app ?? throw new InvalidOperationException("Application 未初始化");

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

    /// <summary>缓存不能只靠条数兜底：一张 1500x220 的图就是 1.3 MB，条数 × 体积会吃穿内存。</summary>
    private static void TestCacheByteBudget()
    {
        using var cache = new FontRenderCache(capacity: 500, byteBudgetBytes: 32L * 1024 * 1024);
        Check(cache.ByteBudget <= 32L * 1024 * 1024, "字节预算应被采纳");

        var text = new string('W', 40);
        var tasks = new List<Task>();
        for (var i = 0; i < 40; i++)
        {
            var request = new RenderRequest(
                FontKey: "budget-" + i,
                FontFilePath: null,
                Text: text,
                FontSize: 40,
                PixelWidth: 1500,
                PixelHeight: 220);
            tasks.Add(cache.RenderAsync(request));
        }

        Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(30));

        Check(cache.Bytes <= cache.ByteBudget + 1500 * 220 * 4,
            $"缓存字节数应受预算约束，实际 {cache.Bytes / 1024} KB");
        Check(cache.Count < 40, $"40 张 1500x220 位图不应全部留住，实际留了 {cache.Count} 张");
        Check(cache.Count >= 1, "至少应保留最近一张");
    }

    /// <summary>滚动时同一行会被反复求值：同一 CacheKey 的并发请求必须合并成一次真实渲染。</summary>
    private static void TestCacheDedupesInFlight()
    {
        using var cache = new FontRenderCache();
        var request = new RenderRequest(
            FontKey: "dedupe",
            FontFilePath: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "arial.ttf"),
            Text: "Dedupe 0123",
            FontSize: 32,
            PixelWidth: 640,
            PixelHeight: 92);

        var before = cache.RenderedFrames;
        var tasks = new Task<System.Windows.Media.Imaging.BitmapSource>[16];
        for (var i = 0; i < tasks.Length; i++) tasks[i] = cache.RenderAsync(request);
        Task.WaitAll(tasks, TimeSpan.FromSeconds(30));

        var first = tasks[0].Result;
        foreach (var task in tasks)
        {
            Check(ReferenceEquals(task.Result, first), "同一键的所有请求应拿到同一张位图");
        }

        var rendered = cache.RenderedFrames - before;
        Check(rendered == 1, $"16 个并发同键请求只应渲染 1 次，实际 {rendered} 次");
        Check(first.IsFrozen, "返回的位图必须已 Freeze 才能跨线程共享");
    }

    /// <summary>滚动经过的行只该渲染小图；大图必须等到真正被选中才渲染。</summary>
    private static void TestNoLargePreviewWhenIdle()
    {
        var vm = new MainViewModel();
        var arial = new FontItem
        {
            FamilyName = "Arial",
            DisplayName = "Arial",
            FileName = "arial.ttf",
            FullPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "arial.ttf"),
            StyleName = "Regular",
        };
        var simsun = new FontItem
        {
            FamilyName = "SimSun",
            DisplayName = "SimSun",
            FileName = "simsun.ttc",
            FullPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "simsun.ttc"),
            StyleName = "Regular",
        };

        vm.SeedForStressTest(new[] { arial, simsun });
        vm.ApplyFilter();

        // 渲染小图（模拟列表行被实现）
        var small = new RenderRequest(arial.FullPath, arial.FullPath, vm.PreviewText, vm.PreviewFontSize, 1120, 92);
        var smallBitmap = AppServices.RenderCache.RenderAsync(small).GetAwaiter().GetResult();
        arial.PreviewBitmap = smallBitmap;
        simsun.PreviewBitmap = smallBitmap;

        Check(arial.PreviewBitmapLarge is null, "未选中的行不应有大样本位图");
        Check(simsun.PreviewBitmapLarge is null, "未选中的行不应有大样本位图");

        vm.UpdateSelection(new[] { arial });
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (arial.PreviewBitmapLarge is null && DateTime.UtcNow < deadline)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            Thread.Sleep(20);
        }

        Check(arial.PreviewBitmapLarge is not null, "选中之后应渲染出大样本");
        Check(simsun.PreviewBitmapLarge is null, "只该给被选中的那一行渲染大样本");

        vm.UpdateSelection(Array.Empty<FontItem>());
        Check(arial.PreviewBitmapLarge is null, "取消选中后应释放大样本位图");
    }

    /// <summary>逐条 Add 一千次会让 ListBox 重建视图一千次。</summary>
    private static void TestRangeCollection()
    {
        var collection = new FindFont.App.Collections.RangeObservableCollection<int>();
        var events = 0;
        System.Collections.Specialized.NotifyCollectionChangedAction? lastAction = null;
        collection.CollectionChanged += (_, e) =>
        {
            events++;
            lastAction = e.Action;
        };

        var items = Enumerable.Range(0, 1000).ToArray();
        collection.ReplaceAll(items);

        Check(events == 1, $"ReplaceAll 只应触发一次集合通知，实际 {events} 次");
        Check(lastAction == System.Collections.Specialized.NotifyCollectionChangedAction.Reset, "通知应是 Reset");
        Check(collection.Count == 1000, "内容应全部替换进去");

        collection.ReplaceAll(Array.Empty<int>());
        Check(collection.Count == 0, "ReplaceAll 空集合应清空");
    }

    /// <summary>搜索快路径（单次 Contains 打穿拼串）必须与原来的六字段逐项匹配完全等价。</summary>
    private static void TestSearchParity()
    {
        var scanner = new FontScanner();
        var items = scanner.Scan().Items.Take(200).ToList();
        Check(items.Count > 0, "应能扫到字体用于比较");

        var queries = new[] { "sans", "宋", "ARIAL", "regular", "noto", "黑" };

        foreach (var query in queries)
        {
            var fast = items.Where(i => FastMatch(i, query)).Count();
            var legacy = items.Where(i => LegacyMatch(i, query)).Count();
            Check(fast == legacy, $"“{query}”的匹配数应一致：快路径 {fast}，原逻辑 {legacy}");
        }
    }

    private static bool FastMatch(FontItem item, string query) =>
        item.SearchHaystack.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static bool LegacyMatch(FontItem item, string query) =>
        item.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || item.LocalizedName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || item.ChineseName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || item.FamilyName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || item.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
        || item.StyleName.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>
    /// P0 回归：卸载系统字体绝不能删文件。
    /// 判据是「本工具安装台账里有没有这个路径」——台账里没有的一律只清登记。
    /// </summary>
    private static void TestUninstallProtectsSystemFonts()
    {
        var fontDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var systemFont = Path.Combine(fontDir, "arial.ttf");
        Check(File.Exists(systemFont), "本机应存在 arial.ttf 用于验证");

        Check(!FontInstaller.IsManagedInstall(systemFont),
            "系统自带字体不应被判定为「本工具安装」——否则卸载会删掉它");
        Check(!FontInstaller.IsManagedInstall(null), "null 路径必须视为未托管");
        Check(!FontInstaller.IsManagedInstall(string.Empty), "空路径必须视为未托管");
        Check(!FontInstaller.IsManagedInstall(Path.Combine(fontDir, "find-font-不存在-字体-xyz.ttf")),
            "不存在的路径必须视为未托管");

        // 系统字体在界面上仍然可以「卸载」（清登记），但不能是删文件路径。
        var vm = new MainViewModel();
        var system = new FontItem
        {
            FamilyName = "Arial",
            DisplayName = "Arial",
            FileName = "arial.ttf",
            FullPath = systemFont,
            IsSystemFont = true,
        };
        vm.SeedForStressTest(new[] { system });
        vm.UpdateSelection(new[] { system });

        Check(vm.FocusedFont is not null, "选中后应聚焦到该字体");
        Check(vm.FocusedFont!.IsSystemFont, "该项应标记为系统字体");
        Check(vm.ColophonStatus.Contains("不删文件", StringComparison.Ordinal),
            $"档案栏应提示系统字体不删文件，实际：{vm.ColophonStatus}");
    }

    /// <summary>
    /// P0 回归：WOFF/WOFF2 是网页字体，装进字体目录系统也认不出来，
    /// 必须明确拒绝并给出原因，而不是制造「装了但看不见」的假成功。
    /// </summary>
    private static void TestWebFontsRejected()
    {
        foreach (var ext in new[] { ".woff", ".woff2", ".eot" })
        {
            var path = "C:\\tmp\\fake" + ext;
            Check(!FontInstaller.IsSupportedFontFile(path), $"{ext} 不应被当成可安装字体");
            Check(FontInstaller.IsWebFontFile(path), $"{ext} 应被识别为网页字体");
            Check(FontInstaller.DescribeInstallRefusal(path).Contains("网页字体", StringComparison.Ordinal),
                $"{ext} 的拒绝原因应说明是网页字体");
        }

        foreach (var ext in new[] { ".ttf", ".otf", ".ttc", ".otc" })
        {
            var path = "C:\\tmp\\real" + ext;
            Check(FontInstaller.IsSupportedFontFile(path), $"{ext} 应被接受");
            Check(!FontInstaller.IsWebFontFile(path), $"{ext} 不是网页字体");
        }

        // .ttf 曾在这个集合里写了两遍，重复项本身无害，但说明集合被随手改过。
        Check(!FontInstaller.IsSupportedFontFile("C:\\tmp\\noext"), "无扩展名不应被接受");
        Check(!FontInstaller.IsSupportedFontFile(""), "空路径不应被接受");

        // 端到端：真的拿一个 .woff 调 InstallCore，必须在碰字体目录之前就被拒。
        var temp = Path.Combine(Path.GetTempPath(), $"findfont-probe-{Guid.NewGuid():N}.woff");
        try
        {
            File.WriteAllBytes(temp, new byte[] { 0x77, 0x4F, 0x46, 0x46 });
            Check(File.Exists(temp), "临时 woff 应创建成功");

            var log = FontInstaller.InstallCore(temp);
            Check(log.StartsWith("FAIL", StringComparison.Ordinal),
                $"InstallCore 对 woff 应返回 FAIL，实际：{log}");
            Check(log.Contains("网页字体", StringComparison.Ordinal),
                $"拒绝原因应说明是网页字体，实际：{log}");

            var copied = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Fonts), Path.GetFileName(temp));
            Check(!File.Exists(copied), "被拒绝的文件不应出现在系统字体目录里");
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    /// <summary>
    /// P0 回归：注册表值名必须与系统既有格式一致（一份，而不是两份），
    /// 否则字体列表里会出现同名重复项。
    /// </summary>
    private static void TestRegistryValueName()
    {
        Check(FontInstaller.BuildRegistryValueName("arial.ttf") == "arial (TrueType)",
            $"ttf 值名应为 “arial (TrueType)”，实际 {FontInstaller.BuildRegistryValueName("arial.ttf")}");
        Check(FontInstaller.BuildRegistryValueName("simsun.ttc") == "simsun (TrueType collection)",
            $"ttc 值名应带 collection，实际 {FontInstaller.BuildRegistryValueName("simsun.ttc")}");
        Check(FontInstaller.BuildRegistryValueName("SourceHan.otf") == "SourceHan (OpenType)",
            $"otf 值名应为 OpenType，实际 {FontInstaller.BuildRegistryValueName("SourceHan.otf")}");

        // 值名里只应出现一次 "(TrueType" —— 曾经的实现会同时写裸名和带类型名两份。
        var ttf = FontInstaller.BuildRegistryValueName("x.ttf");
        var occurrences = ttf.Split("(TrueType").Length - 1;
        Check(occurrences == 1, $"值名只应包含一次类型后缀，实际 {occurrences} 次：{ttf}");
    }
}
