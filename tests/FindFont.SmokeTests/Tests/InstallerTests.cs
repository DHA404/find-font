using static FindFont.SmokeTests.TestRunner;

namespace FindFont.SmokeTests;

/// <summary>字体安装/卸载的安全回归：系统字体保护、网页字体拒装、注册表值名。</summary>
internal static class InstallerTests
{
    /// <summary>
    /// P0 回归：卸载系统字体绝不能删文件。
    /// 判据是「本工具安装台账里有没有这个路径」——台账里没有的一律只清登记。
    /// </summary>
    internal static void TestUninstallProtectsSystemFonts()
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
    internal static void TestWebFontsRejected()
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
    internal static void TestRegistryValueName()
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
