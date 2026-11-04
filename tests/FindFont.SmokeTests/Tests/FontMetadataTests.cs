using System.Diagnostics;
using FindFont.App.Helpers;

using static FindFont.SmokeTests.TestRunner;

namespace FindFont.SmokeTests;

/// <summary>字体元数据：语言推断、CJK 判定、命名优先级与 sfnt 直读解析。</summary>
internal static class FontMetadataTests
{
    internal static void TestLanguageInference()
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

    internal static void TestContainsCjk()
    {
        Check(FontMetadataReader.ContainsCjk("微软雅黑"), "应识别中文");
        Check(FontMetadataReader.ContainsCjk("游ゴシック"), "应识别日文汉字");
        Check(!FontMetadataReader.ContainsCjk("Segoe UI"), "纯西文不应判为 CJK");
        Check(!FontMetadataReader.ContainsCjk(null), "null 不应判为 CJK");
    }

    internal static void TestBestName()
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

    internal static void TestMetadataReaderAll()
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
}
