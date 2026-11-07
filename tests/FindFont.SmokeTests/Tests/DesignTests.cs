using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;
using static FindFont.SmokeTests.TestRunner;

namespace FindFont.SmokeTests;

/// <summary>
/// 设计规约：间距落在 4px 网格上、正文对比度达到 WCAG AA、品红不滥用。
/// 这三条是界面走查里最容易悄悄退化的地方，用断言钉住。
/// </summary>
internal static class DesignTests
{
    /// <summary>间距必须落在 4px 网格：3px、7px、22px 这类随意值会让人看不出对齐意图。</summary>
    internal static void TestSpacingGrid()
    {
        var app = TestRunner.App ?? throw new InvalidOperationException("Application 未初始化");

        string[] numericKeys =
        {
            "TileSampleHeight", "TileRowHeight", "TileMinWidth", "TileGap", "TilePadding",
            "IndexRailWidth", "ColophonWidth", "PageGutter", "RailPad",
        };

        foreach (var key in numericKeys)
        {
            Check(app.Resources.Contains(key), $"主题里应有尺寸 token {key}");
            var value = Convert.ToDouble(app.Resources[key], CultureInfo.InvariantCulture);
            Check(Math.Abs(value % 4) < 0.01, $"{key}={value} 应落在 4px 网格上");
            Check(value > 0, $"{key} 应为正数");
        }

        // 字号不跟 4px 网格走（正文 15 是排版尺度，不是间距），但必须落在整数磅。
        string[] fontSizeKeys = { "TextBodySize", "TextMetaSize", "TitleSize", "PanelTitleSize", "SectionLabelSize" };
        foreach (var key in fontSizeKeys)
        {
            Check(app.Resources.Contains(key), $"主题里应有字号 token {key}");
            var size = Convert.ToDouble(app.Resources[key], CultureInfo.InvariantCulture);
            Check(size >= 11 && size <= 32, $"{key}={size} 应在 11–32 磅之间");
            Check(Math.Abs(size - Math.Round(size)) < 0.01, $"{key}={size} 应为整数磅");
        }

        // 版心台阶必须单调：标题 > 面板标题 > 正文 > 元信息。
        var title = Convert.ToDouble(app.Resources["TitleSize"], CultureInfo.InvariantCulture);
        var panel = Convert.ToDouble(app.Resources["PanelTitleSize"], CultureInfo.InvariantCulture);
        var body = Convert.ToDouble(app.Resources["TextBodySize"], CultureInfo.InvariantCulture);
        var meta = Convert.ToDouble(app.Resources["TextMetaSize"], CultureInfo.InvariantCulture);
        Check(title > panel, "标题应大于面板标题");
        Check(panel > body, "面板标题应大于正文");
        Check(body > meta, "正文应大于元信息");
    }

    /// <summary>正文与元信息在纸色/白色底上都要达到 WCAG AA（4.5:1）。</summary>
    internal static void TestContrast()
    {
        var app = TestRunner.App ?? throw new InvalidOperationException("Application 未初始化");

        var backgrounds = new[] { "Paper", "SheetRaised", "Sheet" };
        var foregrounds = new[] { "Ink", "InkMuted", "InkFaint", "Proof", "Warn" };

        foreach (var bgName in backgrounds)
        {
            var bg = Brush(app, bgName);
            foreach (var fgName in foregrounds)
            {
                var fg = Brush(app, fgName);
                var ratio = Ratio(fg, bg);
                Check(ratio >= 4.5,
                    $"{fgName} 在 {bgName} 上对比度 {ratio:F2}:1，应达到 AA 的 4.5:1");
            }
        }
    }

    /// <summary>
    /// 品红是校样标记，只允许出现在「选中裁切线」与「缺字字符」两处；
    /// 样本行模板里不该再有带底色的品红药丸。
    /// </summary>
    internal static void TestNoProofWashPills()
    {
        var templates = Path.Combine(RootDirectory, "src", "FindFont.App", "Themes", "Templates.xaml");
        Check(File.Exists(templates), $"找不到 {templates}");

        var text = File.ReadAllText(templates);
        var occurrences = Regex.Matches(text, "ProofWash").Count;
        Check(occurrences == 0, $"样本模板里不应出现 ProofWash 药丸，实际出现 {occurrences} 次");

        // 品红仍要在模板里出现，但只作为文字/裁切线颜色。
        Check(text.Contains("Proof"), "模板仍应使用 Proof 作为强调色");
    }

    private static Color Brush(FindFont.App.App app, string key)
    {
        if (app.Resources[key] is not SolidColorBrush brush)
        {
            throw new InvalidOperationException($"资源 {key} 不是 SolidColorBrush");
        }

        return brush.Color;
    }

    private static double Ratio(Color foreground, Color background)
    {
        var a = Luminance(foreground);
        var b = Luminance(background);
        var lighter = Math.Max(a, b);
        var darker = Math.Min(a, b);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(Color color) => 0.2126 * Channel(color.R)
        + 0.7152 * Channel(color.G)
        + 0.0722 * Channel(color.B);

    private static double Channel(byte value)
    {
        var s = value / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}
