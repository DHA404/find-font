using System.Windows;

namespace FindFont.SmokeTests;

/// <summary>无第三方依赖的冒烟测试：直接跑真实字体库，失败时返回非零退出码。</summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("find-font 冒烟测试");
        Console.WriteLine(new string('-', 60));

        // WPF 一个进程只能有一个 Application；先建好，窗口构造时才能解析主题资源。
        // 构造函数会把 OnStartup 投递成 dispatcher 操作；测试自己泵消息时它会迟到执行，
        // 抢走单实例互斥体并造出一个没人要的窗口。
        FindFont.App.App.StartupSuppressed = true;
        TestRunner.App = new FindFont.App.App();
        TestRunner.App.InitializeComponent();
        // 测试里会反复开关窗口；默认的 OnLastWindowClose 会在最后一个窗口关闭时
        // 触发 Application.OnExit 并释放全局渲染缓存，后面的用例就全拿不到位图了。
        TestRunner.App.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        TestRunner.Run("LanguageInference 关键字推断", FontMetadataTests.TestLanguageInference);
        TestRunner.Run("FontMetadataReader CJK 判定", FontMetadataTests.TestContainsCjk);
        TestRunner.Run("FontItem.BestName 优先级", FontMetadataTests.TestBestName);
        TestRunner.Run("FontScanner 扫描系统字体", ScanningTests.TestScanner);
        TestRunner.Run("FontScanner.EnhanceAsync 补全名称", ScanningTests.TestEnhance);
        TestRunner.Run("FontMetadataReader 直读 sfnt 全量解析", FontMetadataTests.TestMetadataReaderAll);
        TestRunner.Run("FontRenderCache 离屏渲染出位图", RenderingTests.TestRenderCache);
        TestRunner.Run("MainViewModel 搜索与筛选", ViewModelTests.TestViewModelFilter);
        TestRunner.Run("10000 条字体的搜索与筛选压测", ViewModelTests.TestStressFilter);
        TestRunner.Run("CompareViewModel 最多三栏", ViewModelTests.TestCompareViewModel);
        TestRunner.Run("界面资源与主窗口可构造", UiTests.TestWindowsConstruct);
        TestRunner.Run("动效助手遵循系统设置", UiTests.TestMotion);
        TestRunner.Run("窗口材质回退不抛异常", UiTests.TestBackdropFallback);
        TestRunner.Run("尺寸与模板资源", UiTests.TestLayoutAndTemplates);
        TestRunner.Run("版本号来自 version.txt", VersionTests.TestVersionWiring);
        TestRunner.Run("位图缓存受字节预算约束", RenderingTests.TestCacheByteBudget);
        TestRunner.Run("同键并发请求只渲染一次", RenderingTests.TestCacheDedupesInFlight);
        TestRunner.Run("未选中的行不渲染大图", RenderingTests.TestNoLargePreviewWhenIdle);
        TestRunner.Run("RangeObservableCollection 只发一次 Reset", ViewModelTests.TestRangeCollection);
        TestRunner.Run("搜索快路径与原多字段匹配等价", ViewModelTests.TestSearchParity);
        TestRunner.Run("卸载不删系统字体文件", InstallerTests.TestUninstallProtectsSystemFonts);
        TestRunner.Run("网页字体（WOFF）明确拒装", InstallerTests.TestWebFontsRejected);
        TestRunner.Run("字体注册表值名格式正确", InstallerTests.TestRegistryValueName);
        TestRunner.Run("多字面容器展开为多条字体", FontMetadataTests.TestReadAllFaces);
        TestRunner.Run("字符覆盖优先于名字推断语言", FontMetadataTests.TestUnicodeRangeLanguage);
        TestRunner.Run("嵌入权限位翻译成文案", FontMetadataTests.TestDescribeEmbedding);
        TestRunner.Run("单实例互斥体只放行一个进程", InfrastructureTests.TestSingleInstance);
        TestRunner.Run("设置默认值与屏幕可见性判断", InfrastructureTests.TestSettings);
        TestRunner.Run("设置经视图模型往返一致", InfrastructureTests.TestSettingsRoundTripThroughViewModel);
        TestRunner.Run("渲染 DPI 只在合理范围生效", InfrastructureTests.TestRenderDpi);
        TestRunner.Run("DPI 参与位图缓存键", InfrastructureTests.TestDpiParticipatesInCacheKey);
        TestRunner.Run("崩溃日志落盘且带上下文", InfrastructureTests.TestDiagnostics);
        TestRunner.Run("源文件不超过 300 行", InfrastructureTests.TestSourceFileLineLimit);
        TestRunner.Run("四种排序键正确且稳定", FeatureTests.TestSortModes);
        TestRunner.Run("搜索高亮切分命中片段", FeatureTests.TestHighlight);
        TestRunner.Run("拖放安装复用同一分拣路径", FeatureTests.TestDropInstallFilter);
        TestRunner.Run("多字面标注与渲染键", FeatureTests.TestFaceLabel);
        TestRunner.Run("批量读字面与逐字面读取等价", PerformanceTests.TestSingleOpenMatchesPerFace);
        TestRunner.Run("每个字体文件只探测一次", PerformanceTests.TestFileIndexProbesEachFileOnce);
        TestRunner.Run("注册表并行读取顺序稳定", PerformanceTests.TestRegistryOrderIsStable);
        TestRunner.Run("首屏预热覆盖可见行", PerformanceTests.TestPrewarmCoversViewport);
        TestRunner.Run("渲染线程池并行出图", PerformanceTests.TestRenderWorkerPool);

        return TestRunner.Summary();
    }
}
