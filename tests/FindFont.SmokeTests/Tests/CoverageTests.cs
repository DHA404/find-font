using FindFont.App.Helpers;
using FindFont.App.Models;
using FindFont.App.Services;

using static FindFont.SmokeTests.TestRunner;

namespace FindFont.SmokeTests;

/// <summary>
/// 字形覆盖检测：cmap 无映射 / 映射到空字形两类坏字的识别，以及筛选与缓存。
/// 全部用本机真实字体；字体不存在时按「跳过」处理，不制造环境相关的红。
/// </summary>
internal static class CoverageTests
{
    private static readonly string UserFontDir =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Fonts");

    private static readonly int[] SampleProbe = FontCoverageIndex.ToCodepoints("你好，世界").ToArray();

    private static string? FindUserFont(string fileName)
    {
        var path = System.IO.Path.Combine(UserFontDir, fileName);
        return System.IO.File.Exists(path) ? path : null;
    }

    /// <summary>情况 A：码位根本不在 cmap 里。</summary>
    internal static void TestMissingFromMap()
    {
        var path = FindUserFont("流丽行书.ttf");
        if (path is null)
        {
            Check(true, "跳过：本机没有流丽行书.ttf");
            return;
        }

        var you = GlyphCoverageReader.Measure(path, 0, new[] { (int)'你' });
        Check(you.MissingFromCmap == 1, $"「你」应判为无映射，实际 {you.MissingFromCmap}");
        Check(you.EmptyGlyph == 0, $"「你」不应判为空字形，实际 {you.EmptyGlyph}");
        Check(you.Kind == GlyphCoverageKind.MissingFromMap, $"分类应为 MissingFromMap，实际 {you.Kind}");

        var shi = GlyphCoverageReader.Measure(path, 0, new[] { (int)'世' });
        Check(!shi.HasHoles, $"「世」应完整，实际 {shi.Detail}");
    }

    /// <summary>情况 B：有映射但字形轮廓为空。</summary>
    internal static void TestEmptyGlyph()
    {
        var path = FindUserFont("日文毛笔字体.ttf");
        if (path is null)
        {
            Check(true, "跳过：本机没有日文毛笔字体.ttf");
            return;
        }

        var you = GlyphCoverageReader.Measure(path, 0, new[] { (int)'你' });
        Check(you.MissingFromCmap == 0, $"「你」应有映射，实际无映射 {you.MissingFromCmap}");
        Check(you.EmptyGlyph == 1, $"「你」应判为空字形，实际 {you.EmptyGlyph}");
        Check(you.Kind == GlyphCoverageKind.EmptyGlyph, $"分类应为 EmptyGlyph，实际 {you.Kind}");
        Check(you.EmptyGlyphSupported, "glyf 字体应支持空字形检测");

        var hao = GlyphCoverageReader.Measure(path, 0, new[] { (int)'好' });
        Check(!hao.HasHoles, $"「好」应完整，实际 {hao.Detail}");
    }

    /// <summary>两类不能混：西文字体对汉字是「无映射」，不是「空字形」。</summary>
    internal static void TestKindsDoNotMix()
    {
        var path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "arial.ttf");
        if (!System.IO.File.Exists(path))
        {
            Check(true, "跳过：本机没有 arial.ttf");
            return;
        }

        var coverage = GlyphCoverageReader.Measure(path, 0, SampleProbe);
        Check(coverage.MissingFromCmap > 0, "Arial 不覆盖样品串里的汉字，应有无映射");
        Check(coverage.EmptyGlyph == 0, $"Arial 不应报空字形，实际 {coverage.EmptyGlyph}");
        Check(coverage.Kind == GlyphCoverageKind.MissingFromMap, $"分类应为 MissingFromMap，实际 {coverage.Kind}");
    }

    /// <summary>多字面文件按字面分别检测。</summary>
    internal static void TestFacesMeasuredIndependently()
    {
        var path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "simsun.ttc");
        if (!System.IO.File.Exists(path))
        {
            Check(true, "跳过：本机没有 simsun.ttc");
            return;
        }

        var faceCount = FontMetadataReader.CountFaces(path);
        Check(faceCount >= 2, $"simsun.ttc 应至少两个字面，实际 {faceCount}");

        var first = GlyphCoverageReader.Measure(path, 0, SampleProbe);
        var second = GlyphCoverageReader.Measure(path, 1, SampleProbe);
        Check(first.Probed == second.Probed, "两个字面应探测同样的码位数");
        Check(first.Probed == SampleProbe.Length, $"探测码位数应为 {SampleProbe.Length}，实际 {first.Probed}");
        Check(!first.HasHoles, $"SimSun 应覆盖样品串，实际 {first.Detail}");
        Check(!second.HasHoles, $"NSimSun 应覆盖样品串，实际 {second.Detail}");
    }

    /// <summary>CFF 字体只报无映射，不判空字形。</summary>
    internal static void TestCffSkipsEmptyGlyph()
    {
        var path = FindTrueCffFont();
        if (path is null)
        {
            Check(true, "跳过：本机没有纯 CFF 轮廓的字体");
            return;
        }

        var coverage = GlyphCoverageReader.Measure(path, 0, SampleProbe);
        Check(!coverage.EmptyGlyphSupported, "CFF 字体不应宣称支持空字形检测");
        Check(coverage.EmptyGlyph == 0, "CFF 字体不应报出空字形");
        Check(coverage.Kind != GlyphCoverageKind.EmptyGlyph, "CFF 字体分类不应是 EmptyGlyph");
    }

    /// <summary>
    /// 找一个「真的没有 glyf/loca」的 CFF 字体。注意 .otf 后缀不代表 CFF ——
    /// 本机就有 TrueType 轮廓存成 .otf 的（Noto Sans SC 那几个），它们能正常判空字形。
    /// </summary>
    private static string? FindTrueCffFont()
    {
        try
        {
            if (!System.IO.Directory.Exists(UserFontDir)) return null;

            foreach (var path in System.IO.Directory.EnumerateFiles(UserFontDir, "*.otf"))
            {
                using var stream = new System.IO.FileStream(
                    path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                    System.IO.FileShare.ReadWrite, 4096, System.IO.FileOptions.RandomAccess);
                var tables = SfntReader.ReadTables(stream);
                if (tables is { HasCff: true, LocaOffset: < 0 }) return path;
            }
        }
        catch (Exception ex) when (FontMetadataReader.IsRecoverable(ex))
        {
            return null;
        }

        return null;
    }

    /// <summary>同样探测集第二次不该再读盘（靠 IsRecoverable 之外的缓存命中率说话）。</summary>
    internal static void TestCoverageCacheReused()
    {
        var index = new FontCoverageIndex();
        var items = new List<FontItem>();
        foreach (var item in AppServices.Scanner.Scan().Items)
        {
            if (item.IsBroken) continue;
            items.Add(item);
            if (items.Count >= 20) break;
        }

        Check(items.Count > 0, "应至少探测到 1 个字体");
        if (items.Count == 0) return;

        var probe = new[] { (int)'A', (int)'你' };
        var first = index.Measure(items, probe, CancellationToken.None);
        Check(first == items.Count, $"首次应全部新量，实际 {first}");

        var second = index.Measure(items, probe, CancellationToken.None);
        Check(second == 0, $"同样探测集第二次不应再读盘，实际 {second}");

        foreach (var item in items)
        {
            Check(index.TryGetCached(item) is not null, "缓存里应能取到结果");
            break;
        }
    }

    /// <summary>两个缺字筛选开关各自只留对应类型，且都打开是 AND。</summary>
    internal static void TestCoverageFilters()
    {
        var missingMap = new FontItem
        {
            FamilyName = "MapMissing", DisplayName = "MapMissing", FullPath = "x1",
            Coverage = new GlyphCoverage(GlyphCoverageKind.MissingFromMap, 20, 3, 0,
                new[] { '你' }, Array.Empty<char>(), true),
        };
        var emptyOut = new FontItem
        {
            FamilyName = "EmptyGlyph", DisplayName = "EmptyGlyph", FullPath = "x2",
            Coverage = new GlyphCoverage(GlyphCoverageKind.EmptyGlyph, 20, 0, 2,
                Array.Empty<char>(), new[] { '好' }, true),
        };
        var clean = new FontItem
        {
            FamilyName = "Clean", DisplayName = "Clean", FullPath = "x3",
            Coverage = new GlyphCoverage(GlyphCoverageKind.Complete, 20, 0, 0,
                Array.Empty<char>(), Array.Empty<char>(), true),
        };
        var uncheckedFont = new FontItem { FamilyName = "Unknown", DisplayName = "Unknown", FullPath = "x4" };

        Check(missingMap.HasMissingFromCmap && !missingMap.HasEmptyGlyph, "缺映射项只应命中缺映射");
        Check(emptyOut.HasEmptyGlyph && !emptyOut.HasMissingFromCmap, "空字形项只应命中空字形");
        Check(!clean.HasCoverageHoles, "完整项不应有缺字");
        Check(!uncheckedFont.HasCoverageHoles, "未检测项不应被算作有缺字");
        Check(missingMap.Coverage!.Label.StartsWith("缺字", StringComparison.Ordinal), "徽标文案应以「缺字」开头");
        Check(missingMap.Coverage.Detail.Contains("无映射", StringComparison.Ordinal), "细分文案应含「无映射」");
        Check(emptyOut.Coverage!.Detail.Contains("空字形", StringComparison.Ordinal), "细分文案应含「空字形」");

        if (TestRunner.App is null)
        {
            Check(true, "跳过：没有 Application 实例");
            return;
        }

        var vm = new FindFont.App.ViewModels.MainViewModel();
        vm.SeedForStressTest(new[] { missingMap, emptyOut, clean, uncheckedFont });

        vm.ShowMissingMapOnly = true;
        Check(vm.VisibleFonts.Count == 1, $"只看缺映射应剩 1 项，实际 {vm.VisibleFonts.Count}");
        Check(vm.VisibleFonts[0].FamilyName == "MapMissing", "只看缺映射应剩 MapMissing");

        vm.ShowMissingMapOnly = false;
        vm.ShowEmptyGlyphOnly = true;
        Check(vm.VisibleFonts.Count == 1, $"只看空字形应剩 1 项，实际 {vm.VisibleFonts.Count}");
        Check(vm.VisibleFonts[0].FamilyName == "EmptyGlyph", "只看空字形应剩 EmptyGlyph");

        vm.ShowMissingMapOnly = true;
        Check(vm.VisibleFonts.Count == 0, "两个开关同时打开（AND）应没有交集");

        vm.ShowMissingMapOnly = false;
        vm.ShowEmptyGlyphOnly = false;
        vm.ShowHealthyOnly = true;
        Check(vm.VisibleFonts.Count == 1, $"只看正常应剩 1 项，实际 {vm.VisibleFonts.Count}");
        Check(vm.VisibleFonts[0].FamilyName == "Clean", "只看正常应只留 Clean（未检测的不算正常）");

        vm.ShowHealthyOnly = false;
        Check(vm.VisibleFonts.Count == 4, "全部关掉应恢复 4 项");
    }
}
