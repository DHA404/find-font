using static FindFont.SmokeTests.TestRunner;

namespace FindFont.SmokeTests;

/// <summary>系统字体扫描与本地化名补全。</summary>
internal static class ScanningTests
{
    internal static void TestScanner()
    {
        var scanner = new FontScanner();
        var result = scanner.Scan();
        Console.WriteLine($"        注册表项 {result.RegistryEntries}，字体 {result.Items.Count}，" +
                          $"缺失文件 {result.MissingFiles}，耗时 {result.Elapsed.TotalMilliseconds:F0} ms");
        Check(result.Items.Count > 0, "系统应当至少扫描到 1 个字体");
        Check(result.Items.All(i => !string.IsNullOrWhiteSpace(i.FileName)), "每项都应有文件名");
        Check(result.Items.All(i => !string.IsNullOrWhiteSpace(i.DisplayName)), "每项都应有显示名");

        // 同一个字面不能在列表里出现两次。注册表的 64/32 视图各有一份同名登记，
        // 用户字体目录又与注册表指向同一批文件，若按「家族名+样式名」去重会残留重复。
        // 自 0.5.0 起 .ttc/.otc 会展开成多个字面，所以键是「路径 + 字面序号」。
        var sameFace = result.Items
            .Where(i => i.FullPath.Length > 0)
            .GroupBy(i => (i.FullPath.ToLowerInvariant(), i.FaceIndex))
            .Count(g => g.Count() > 1);
        Check(sameFace == 0, $"同一文件的同一字面不应出现多次，实际 {sameFace} 处重复");

        var multiFaceFiles = result.Items
            .Where(i => i.FaceCount > 1)
            .GroupBy(i => i.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Console.WriteLine($"        多字面容器 {multiFaceFiles.Count} 个文件，扩展出 " +
                          $"{multiFaceFiles.Sum(g => g.Count())} 个字体");
        Check(multiFaceFiles.Count > 0, "系统上应存在 .ttc/.otc 多字面容器");
        Check(multiFaceFiles.All(g => g.Select(i => i.FaceIndex).Distinct().Count() == g.Count()),
            "同一个容器的每个字面只应出现一次");
    }

    internal static void TestEnhance()
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
}
