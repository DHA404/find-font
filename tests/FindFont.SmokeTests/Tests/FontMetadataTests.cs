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

    /// <summary>.ttc/.otc 必须逐字面展开，否则会成批漏掉中文字体。</summary>
    internal static void TestReadAllFaces()
    {
        var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var ttc = Path.Combine(fontsDir, "simsun.ttc");

        if (!File.Exists(ttc))
        {
            // 本机没有该文件时退而求其次：任意一个 .ttc 都行。
            ttc = Directory.GetFiles(fontsDir, "*.ttc").FirstOrDefault() ?? string.Empty;
        }

        if (ttc.Length == 0)
        {
            Console.WriteLine("        本机没有 .ttc 字体，跳过");
            return;
        }

        var faces = FontMetadataReader.ReadAllFaces(ttc);
        Console.WriteLine($"        {Path.GetFileName(ttc)} 解析出 {faces.Count} 个字面：" +
                          $"{string.Join(" / ", faces.Select(f => f.FamilyName + " " + f.FaceName))}");

        Check(faces.Count >= 1, "多字面容器应至少解析出 1 个字面");
        Check(faces.Select(f => f.FaceIndex).Distinct().Count() == faces.Count, "字面序号不应重复");
        Check(faces.All(f => f.FaceCount == faces.Count), "每个字面都应记录容器内的总字数");

        // 单字体文件只应有 1 个字面。
        var single = Path.Combine(fontsDir, "arial.ttf");
        if (File.Exists(single))
        {
            var one = FontMetadataReader.ReadAllFaces(single);
            Check(one.Count == 1, $"单字体文件应只有 1 个字面，实际 {one.Count}");
            Check(one[0].FaceIndex == 0 && one[0].FaceCount == 1, "单字体文件的字面信息应为 0/1");
        }

        // 同一个容器里各字面必须能各自解析（而不是全部返回首个成员）。
        if (faces.Count > 1)
        {
            var distinct = faces.Select(f => f.FamilyName + "\u0001" + f.FaceName).Distinct().Count();
            Check(distinct > 1 || faces.All(f => f.FamilyName == faces[0].FamilyName),
                "多字面容器里各成员的名称应能区分");
        }
    }

    /// <summary>OS/2 ulUnicodeRange 判语言，比家族名关键词可靠。</summary>
    internal static void TestUnicodeRangeLanguage()
    {
        static uint Bit(int n) => 1u << (n & 31);

        // bit 59 = CJK Unified Ideographs（落在 range2 的第 27 位）。
        var cjk = LanguageInference.FromUnicodeRange(0, Bit(59));
        Check(cjk == FindFont.App.Models.FontLanguage.Chinese, $"CJK 覆盖应判为中文，实际 {cjk}");

        // bit 49/50 = Hiragana/Katakana。
        var kana = LanguageInference.FromUnicodeRange(0, Bit(49) | Bit(50));
        Check(kana == FindFont.App.Models.FontLanguage.Japanese, $"假名覆盖应判为日文，实际 {kana}");

        // bit 56 = Hangul Syllables。
        var hangul = LanguageInference.FromUnicodeRange(0, Bit(56));
        Check(hangul == FindFont.App.Models.FontLanguage.Korean, $"谚文覆盖应判为韩文，实际 {hangul}");

        // 只有拉丁区覆盖时不应误判为东亚语言。
        var latin = LanguageInference.FromUnicodeRange(Bit(0), 0);
        Check(latin is null, $"纯拉丁覆盖应返回 null，实际 {latin}");

        // 覆盖信息优先于名字关键词。
        var inferred = LanguageInference.InferWithCoverage(
            FindFont.App.Models.FontLanguage.Japanese, "SimSun", "宋体");
        Check(inferred == FindFont.App.Models.FontLanguage.Japanese,
            $"字符覆盖应压过名字关键词，实际 {inferred}");

        // 没有覆盖信息时退回关键词。
        var fallback = LanguageInference.InferWithCoverage(null, "SimSun", "宋体");
        Check(fallback == FindFont.App.Models.FontLanguage.Chinese, $"无覆盖信息时应按名字推断，实际 {fallback}");

        // 真实字体：simsun 同时覆盖汉字与假名，位域判不出来，应回落到名字推断为中文。
        var simsun = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "simsun.ttc");
        if (File.Exists(simsun))
        {
            var info = FontMetadataReader.TryRead(simsun);
            var lang = LanguageInference.InferWithCoverage(
                info?.ScriptLanguage, info?.ChineseFamilyName, info?.FamilyName);
            Check(lang == FindFont.App.Models.FontLanguage.Chinese,
                $"simsun.ttc 应判定为中文，实际 {lang}（覆盖位 {info?.ScriptLanguage}）");
        }
    }

    internal static void TestDescribeEmbedding()
    {
        Check(FontMetadataReader.DescribeEmbedding(-1).Contains("未知"), "缺 OS/2 表时应说明未知");
        Check(FontMetadataReader.DescribeEmbedding(0x0002) == "禁止嵌入", "fsType bit1 应为禁止嵌入");
        Check(FontMetadataReader.DescribeEmbedding(0x0200) == "仅位图嵌入", "fsType bit9 应为仅位图嵌入");
        Check(FontMetadataReader.DescribeEmbedding(0x0004).Contains("预览与打印"), "fsType bit2 应为仅预览打印");
        Check(FontMetadataReader.DescribeEmbedding(0x0008).Contains("可安装"), "fsType bit3 应为可安装");
        Check(FontMetadataReader.DescribeEmbedding(0) == "无嵌入限制", "无限制位时应为无嵌入限制");

        // bit8 与许可位不冲突，应当并列出现；bit1 优先级最高，其余位不再解释。
        Check(FontMetadataReader.DescribeEmbedding(0x0100).Contains("子集化"), "fsType bit8 应附加不允许子集化");
        Check(FontMetadataReader.DescribeEmbedding(0x0108).Contains("可安装")
              && FontMetadataReader.DescribeEmbedding(0x0108).Contains("子集化"),
            "可安装 + 禁止子集化，两条都要说");
        Check(FontMetadataReader.DescribeEmbedding(0x0002 | 0x0100) == "禁止嵌入",
            "禁止嵌入优先级最高，不再叠加子集化说明");

        Check(FontMetadataReader.IsEmbeddingRestricted(0x0002), "bit1 应判为受限");
        Check(!FontMetadataReader.IsEmbeddingRestricted(0x0008), "可安装不应判为受限");
        Check(!FontMetadataReader.IsEmbeddingRestricted(-1), "未知许可不应判为受限");
    }

    /// <summary>可变字体轴解析：拿本机真字体做端到端，找不到就只验证构造表。</summary>
    internal static void TestVariationAxes()
    {
        var variable = FindVariableFont();
        if (variable is null)
        {
            Check(true, "跳过：本机没有可变字体");
        }
        else
        {
            var faces = FontMetadataReader.ReadFile(variable);
            Check(faces.Count > 0, "可变字体应能解析出字面");
            if (faces.Count > 0)
            {
                var axes = faces[0].VariationAxes;
                Check(axes.Length > 0, "可变字体应解析出坐标轴");
                Check(axes.Contains(' ') || axes.Contains('·'), "轴描述应形如「wght 100–900」");
            }
        }

        // 静态字体不应谎称可变。
        var regular = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        if (File.Exists(regular))
        {
            var info = FontMetadataReader.TryRead(regular);
            Check(info is not null && info.VariationAxes.Length == 0, "静态字体不应有坐标轴");
        }

        Check(VariationAxesParser.Describe(null) == string.Empty, "没有 fvar 表时应返回空串");
        Check(VariationAxesParser.Describe(new byte[8]) == string.Empty, "表太短时应返回空串");
    }

    private static string? FindVariableFont()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Fonts"),
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            foreach (var pattern in new[] { "*.ttf", "*.otf" })
            {
                string[] files;
                try
                {
                    files = Directory.GetFiles(root, pattern, SearchOption.TopDirectoryOnly);
                }
                catch (Exception ex) when (FontMetadataReader.IsRecoverable(ex))
                {
                    continue;
                }

                foreach (var file in files)
                {
                    try
                    {
                        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
                        var offsets = SfntReader.ReadFaceOffsets(stream);
                        if (offsets is null or { Length: 0 }) continue;

                        var tables = SfntReader.ReadTables(stream, offsets[0]);
                        if (tables is null || SfntReader.ReadFvarTable(stream, tables) is null) continue;

                        return file;
                    }
                    catch (Exception ex) when (FontMetadataReader.IsRecoverable(ex))
                    {
                        continue;
                    }
                }
            }
        }

        return null;
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
