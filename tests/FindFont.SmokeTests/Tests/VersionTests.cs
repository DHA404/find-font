using static FindFont.SmokeTests.TestRunner;

namespace FindFont.SmokeTests;

/// <summary>版本号单一来源：version.txt → 程序集 → 视图模型。</summary>
internal static class VersionTests
{
    internal static void TestVersionWiring()
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
}
