using System.Collections.Concurrent;
using System.IO;
using Microsoft.Win32;

namespace FindFont.App.Services;

/// <summary>注册表里的一条字体登记。</summary>
internal sealed record FontRegistryEntry(string Name, string? Value, bool IsSystem);

/// <summary>
/// 注册表字体登记信息读取器：
/// 枚举 <c>SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts</c> 的 64/32 位视图。
/// </summary>
internal static class FontRegistryReader
{
    private const string FontsKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";

    private static readonly (RegistryHive Hive, RegistryView View)[] Views =
    {
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Registry64),
        (RegistryHive.CurrentUser, RegistryView.Registry32),
    };

    /// <summary>
    /// 收集 HKLM / HKCU 的 64 位与 32 位视图里的全部登记。
    /// <para>
    /// 四个视图互不依赖，并行读；结果按视图固定顺序拼接，保证输出可复现
    /// （注册表读本身是纯 IO，并行只是把四次往返叠在一起）。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<FontRegistryEntry> ReadEntries()
    {
        var buckets = new List<FontRegistryEntry>[Views.Length];
        Parallel.For(0, Views.Length, i =>
        {
            buckets[i] = ReadView(Views[i].Hive, Views[i].View);
        });

        var total = 0;
        foreach (var bucket in buckets) total += bucket.Count;

        var all = new List<FontRegistryEntry>(total);
        foreach (var bucket in buckets) all.AddRange(bucket);
        return all;
    }

    /// <summary>兼容旧调用：逐条回调。</summary>
    internal static void ReadAll(Action<string, string?, bool> add)
    {
        foreach (var entry in ReadEntries())
        {
            add(entry.Name, entry.Value, entry.IsSystem);
        }
    }

    private static List<FontRegistryEntry> ReadView(RegistryHive hive, RegistryView view)
    {
        var entries = new List<FontRegistryEntry>(512);
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(FontsKeyPath, writable: false);
            if (key is null) return entries;

            var isSystem = hive == RegistryHive.LocalMachine;
            foreach (var name in key.GetValueNames())
            {
                entries.Add(new FontRegistryEntry(name, key.GetValue(name) as string, isSystem));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // 某个注册表视图不可读时忽略，其余视图继续。
        }

        return entries;
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
