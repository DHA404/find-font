namespace FindFont.SmokeTests;

/// <summary>测试分层：决定一个用例在哪种环境下必须跑。</summary>
internal enum TestTier
{
    /// <summary>纯逻辑，不碰字体文件、不开窗口。任何机器上都应该是毫秒级。</summary>
    Unit,

    /// <summary>要读本机字体库 / 注册表 / 临时文件。依赖机器上真的装了字体。</summary>
    Integration,

    /// <summary>要构造 WPF 窗口、解析主题资源。需要桌面会话。</summary>
    Ui,
}

/// <summary>
/// 用例名 → 层次。查不到的一律算集成测试——宁可多跑几个依赖环境的用例，
/// 也不要把环境依赖偷偷塞进「纯逻辑」那一层。
/// </summary>
internal static class TestTiers
{
    private static readonly HashSet<string> UnitTests = new(StringComparer.Ordinal)
    {
        "LanguageInference 关键字推断",
        "FontMetadataReader CJK 判定",
        "FontItem.BestName 优先级",
        "CompareViewModel 最多三栏",
        "版本号来自 version.txt",
        "RangeObservableCollection 只发一次 Reset",
        "字体注册表值名格式正确",
        "用户级安装路径走 HKCU",
        "嵌入权限位翻译成文案",
        "字符覆盖优先于名字推断语言",
        "设置默认值与屏幕可见性判断",
        "设置经视图模型往返一致",
        "渲染 DPI 只在合理范围生效",
        "DPI 参与位图缓存键",
        "崩溃日志落盘且带上下文",
        "多列分行与筛选顺序一致",
        "列数随窗口宽度收敛",
        "瓦片选中驱动既有筛选语义",
        "间距落在 4px 网格",
        "文字对比度达到 AA",
        "品红不做药丸底色",
        "源文件不超过 300 行",
        "四种排序键正确且稳定",
        "搜索高亮切分命中片段",
        "拖放安装复用同一分拣路径",
        "多字面标注与渲染键",
        "单实例互斥体只放行一个进程",
    };

    private static readonly HashSet<string> UiTests = new(StringComparer.Ordinal)
    {
        "界面资源与主窗口可构造",
        "动效助手遵循系统设置",
        "窗口材质回退不抛异常",
        "尺寸与模板资源",
    };

    internal static TestTier Of(string name) =>
        UnitTests.Contains(name) ? TestTier.Unit :
        UiTests.Contains(name) ? TestTier.Ui :
        TestTier.Integration;

    /// <summary>把命令行参数翻译成层过滤；认不出来时返回 null（不筛选）。</summary>
    internal static TestTier? Parse(string? arg) => arg?.Trim().ToLowerInvariant() switch
    {
        "unit" or "u" => TestTier.Unit,
        "integration" or "i" => TestTier.Integration,
        "ui" => TestTier.Ui,
        _ => null,
    };

    internal static string Describe(TestTier tier) => tier switch
    {
        TestTier.Unit => "unit",
        TestTier.Ui => "ui",
        _ => "integration",
    };
}
