using static FindFont.SmokeTests.TestRunner;

namespace FindFont.SmokeTests;

/// <summary>阶段三新增功能：排序、搜索高亮、拖放安装、字面标注。</summary>
internal static class FeatureTests
{
    /// <summary>四种排序各按自己的键生效，且都保持「键相同再按名称」的稳定次序。</summary>
    internal static void TestSortModes()
    {
        EnsureApp();

        var items = new List<FontItem>
        {
            new() { FamilyName = "Bravo", DisplayName = "Bravo", FileSize = 300, ModifiedAt = new DateTime(2020, 1, 1), Language = FontLanguage.Latin },
            new() { FamilyName = "Alpha", DisplayName = "Alpha", FileSize = 100, ModifiedAt = new DateTime(2023, 1, 1), Language = FontLanguage.Chinese },
            new() { FamilyName = "Charlie", DisplayName = "Charlie", FileSize = 200, ModifiedAt = new DateTime(2021, 1, 1), Language = FontLanguage.Latin },
        };

        MainViewModel.SortItems(items, FontSortMode.Name);
        Check(Order(items) == "Alpha,Bravo,Charlie", $"按名称排序应升序，实际 {Order(items)}");

        MainViewModel.SortItems(items, FontSortMode.Recent);
        Check(Order(items) == "Alpha,Charlie,Bravo", $"按最近修改应降序，实际 {Order(items)}");

        MainViewModel.SortItems(items, FontSortMode.Size);
        Check(Order(items) == "Bravo,Charlie,Alpha", $"按文件大小应降序，实际 {Order(items)}");

        MainViewModel.SortItems(items, FontSortMode.Language);
        // Latin 排在 Chinese 之前（枚举顺序），同语言再按名称。
        Check(Order(items) == "Bravo,Charlie,Alpha", $"按语言应先按语言码再按名称，实际 {Order(items)}");

        // 键完全相同时必须按名称兜底，排序结果才可复现。
        var ties = new List<FontItem>
        {
            new() { FamilyName = "Zulu", DisplayName = "Zulu", FileSize = 42, ModifiedAt = DateTime.MinValue },
            new() { FamilyName = "Mike", DisplayName = "Mike", FileSize = 42, ModifiedAt = DateTime.MinValue },
        };
        MainViewModel.SortItems(ties, FontSortMode.Size);
        Check(Order(ties) == "Mike,Zulu", $"大小相同时应按名称兜底，实际 {Order(ties)}");
    }

    private static string Order(IEnumerable<FontItem> items) => string.Join(",", items.Select(i => i.FamilyName));

    /// <summary>搜索高亮切出「前 / 命中 / 后」三段，命中片段的原始大小写必须保留。</summary>
    internal static void TestHighlight()
    {
        var item = new FontItem
        {
            FamilyName = "Arial Rounded MT Bold",
            DisplayName = "Arial Rounded MT Bold",
            FileName = "ARIALRBD.TTF",
        };

        item.SetHighlight("rounded");
        Check(item.HasMatch, "应识别出命中");
        Check(item.MatchHit == "Rounded", $"命中片段应保留原大小写，实际 “{item.MatchHit}”");
        Check(item.MatchBefore.EndsWith("Arial ", StringComparison.Ordinal), $"前缀应到命中前一位，实际 “{item.MatchBefore}”");
        Check(item.MatchAfter.Contains("MT Bold", StringComparison.Ordinal), $"后缀应接命中之后，实际 “{item.MatchAfter}”");

        item.SetHighlight("  ");
        Check(!item.HasMatch && item.MatchHit.Length == 0, "空白查询应清空高亮");

        item.SetHighlight("zzz");
        Check(!item.HasMatch, "无命中时不应留残段");

        // 只有文件名命中的字体也要给出片段，否则用户看不出为什么它被搜出来了。
        item.SetHighlight("arialrbd");
        Check(item.HasMatch, "仅文件名命中时也应高亮");
    }

    /// <summary>拖放与文件对话框走同一条安装路径：网页字体被拒，且拒绝原因可读。</summary>
    internal static void TestDropInstallFilter()
    {
        EnsureApp();
        var vm = new MainViewModel();

        // 空列表直接返回，不该抛异常、也不该改动状态。
        vm.InstallFilesAsync(Array.Empty<string>()).GetAwaiter().GetResult();
        Check(true, "空拖放列表应安全返回");

        var temp = Path.Combine(Path.GetTempPath(), "find-font-drop-" + Guid.NewGuid().ToString("N") + ".woff2");
        try
        {
            File.WriteAllBytes(temp, new byte[] { 0x77, 0x4F, 0x46, 0x32 });
            vm.InstallFilesAsync(new[] { temp }).GetAwaiter().GetResult();
            Check(vm.StatusText.Contains("网页字体", StringComparison.Ordinal),
                $"网页字体应被拒并说明原因，实际状态 “{vm.StatusText}”");
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    /// <summary>单字体文件不显示字面标注，多字面容器才显示「第 N/M 面」。</summary>
    internal static void TestFaceLabel()
    {
        var single = new FontItem { FamilyName = "Solo", FaceIndex = 0, FaceCount = 1 };
        Check(!single.HasMultipleFaces && single.FaceLabel.Length == 0, "单字面文件不应有字面标注");

        var multi = new FontItem { FamilyName = "SimSun", FaceIndex = 1, FaceCount = 3 };
        Check(multi.HasMultipleFaces, "多字面容器应标记出来");
        Check(multi.FaceLabel == "第 2/3 面", $"字面标注应从 1 开始编号，实际 “{multi.FaceLabel}”");

        Check(multi.RenderKey.EndsWith("#1", StringComparison.Ordinal),
            $"多字面的渲染键必须带字面号，否则会渲染错成员，实际 “{multi.RenderKey}”");
        Check(single.RenderKey == single.FullPath, "单字面的渲染键应与路径一致");
    }
}
