using System.IO;
using Microsoft.Win32;

namespace FindFont.App.Services;

/// <summary>
/// 注册表字体登记信息读取器：
/// 枚举 <c>SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts</c> 的 64/32 位视图。
/// </summary>
internal static class FontRegistryReader
{
    private const string FontsKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";

    /// <summary>依次读取 HKLM / HKCU 的 64 位与 32 位视图，每条登记回调一次。</summary>
    internal static void ReadAll(Action<string, string?, bool> add)
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                TryReadHive(hive, view, add);
            }
        }
    }

    private static void TryReadHive(RegistryHive hive, RegistryView view, Action<string, string?, bool> add)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(FontsKeyPath, writable: false);
            if (key is null) return;

            var isSystem = hive == RegistryHive.LocalMachine;
            foreach (var name in key.GetValueNames())
            {
                var value = key.GetValue(name) as string;
                add(name, value, isSystem);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // 某个注册表视图不可读时忽略，其余视图继续。
        }
    }

    /// <summary>注册表项名形如 "微软雅黑 &amp; 微软雅黑 Light (TrueType)"，去掉后缀并与 "&amp;" 取首个。</summary>
    internal static string CleanRegistryName(string registryName)
    {
        var name = registryName;
        var paren = name.IndexOf('(');
        if (paren > 0) name = name[..paren];
        name = name.Replace("&", "&").Trim();

        var amp = name.IndexOf('&');
        if (amp > 0) name = name[..amp].Trim();

        return string.IsNullOrWhiteSpace(name) ? registryName : name;
    }

    internal static string GuessStyleFromName(string registryName)
    {
        var lowered = registryName.ToLowerInvariant();
        if (lowered.Contains("bold") || lowered.Contains("粗")) return "Bold";
        if (lowered.Contains("light") || lowered.Contains("细")) return "Light";
        if (lowered.Contains("italic") || lowered.Contains("斜")) return "Italic";
        if (lowered.Contains("medium") || lowered.Contains("中等")) return "Medium";
        return "Regular";
    }
}
