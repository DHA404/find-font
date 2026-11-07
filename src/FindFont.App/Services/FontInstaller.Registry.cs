using System.IO;
using Microsoft.Win32;

namespace FindFont.App.Services;

/// <summary>
/// 字体注册表的读写。拆出此文件只为守住单文件 300 行的规约；
/// 关键在于「同一个文件只登记一条」与「值名带类型后缀」，两条都写在这里。
/// </summary>
public static partial class FontInstaller
{
    // ------------------------------------------------------------ 注册表

    /// <summary>注册表值名的标准形态：<c>Arial (TrueType)</c> / <c>SourceHan (OpenType)</c>。</summary>
    internal static string BuildRegistryValueName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var typeName = ext switch
        {
            ".ttc" or ".otc" => "(TrueType collection)",
            ".otf" => "(OpenType)",
            _ => "(TrueType)",
        };
        return $"{stem} {typeName}";
    }

    private static bool RegisterInRegistry(string fileName, RegistryHive hive, RegistryView view)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.CreateSubKey(FontsKeyPath, writable: true);
            if (key is null) return false;

            // 同一个文件已经登记过（哪怕是别的名字）就不再写第二条，
            // 否则系统字体列表里会出现重复项。
            foreach (var existingName in key.GetValueNames())
            {
                var existingValue = key.GetValue(existingName) as string;
                if (string.IsNullOrWhiteSpace(existingValue)) continue;
                if (string.Equals(Path.GetFileName(existingValue), fileName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            key.SetValue(BuildRegistryValueName(fileName), fileName, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool RemoveFromRegistry(string displayName, string fileName, RegistryHive hive, RegistryView view)
    {
        var removed = false;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(FontsKeyPath, writable: true);
            if (key is null) return false;

            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(fileName)) targets.Add(fileName);
            if (!string.IsNullOrWhiteSpace(displayName)) targets.Add(displayName);

            var names = key.GetValueNames();
            foreach (var name in names)
            {
                var value = key.GetValue(name) as string;
                var matchByName = targets.Contains(name);
                var matchByFile = !string.IsNullOrWhiteSpace(value)
                                  && !string.IsNullOrWhiteSpace(fileName)
                                  && string.Equals(Path.GetFileName(value), fileName, StringComparison.OrdinalIgnoreCase);

                if (!matchByName && !matchByFile) continue;

                try
                {
                    key.DeleteValue(name, throwOnMissingValue: false);
                    removed = true;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // 某一项删不掉时继续处理其余项。
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }

        return removed;
    }
}
