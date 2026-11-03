using System.Text;
using Microsoft.Win32;
using FindFont.App.Helpers;

namespace FindFont.App.Services;

/// <summary>安装 / 卸载 / 坏字体清理的底层实现（在提权进程中执行，也可直接调用）。</summary>
public static class FontInstaller
{
    private const string FontsKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";

    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".ttf", ".otf", ".ttc", ".otc", ".ttf", ".woff", ".woff2" };

    public static bool IsSupportedFontFile(string path) =>
        !string.IsNullOrWhiteSpace(path) && SupportedExtensions.Contains(Path.GetExtension(path));

    /// <summary>安装单个字体文件到系统字体目录并注册（需要管理员权限）。</summary>
    public static string InstallCore(string sourcePath)
    {
        if (!File.Exists(sourcePath)) return $"SKIP {sourcePath}: 文件不存在";
        if (!IsSupportedFontFile(sourcePath)) return $"SKIP {sourcePath}: 不支持的字体格式";

        var fileName = Path.GetFileName(sourcePath);
        var fontDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var target = Path.Combine(fontDir, fileName);

        if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.Copy(sourcePath, target, overwrite: true);
            }
            catch (IOException ex)
            {
                return $"FAIL {fileName}: 复制失败 {ex.Message}";
            }
            catch (UnauthorizedAccessException ex)
            {
                return $"FAIL {fileName}: 无权限写入字体目录 {ex.Message}";
            }
        }

        var added = NativeMethods.AddFontResourceEx(target, NativeMethods.FR_DEFAULT, IntPtr.Zero);
        RegisterInRegistry(fileName, fileName, RegistryView.Registry64);
        RegisterInRegistry(fileName, fileName, RegistryView.Registry32);

        return added > 0
            ? $"OK 已安装 {fileName}"
            : $"WARN {fileName}: 已复制但系统未能加载该字体（可能是坏字体）";
    }

    /// <summary>卸载字体：按注册名或字体文件路径移除注册表项与文件（需要管理员权限）。</summary>
    public static string UninstallCore(string entry)
    {
        // entry 可以是 "显示名|完整路径" 或单独的路径。
        var parts = entry.Split('|', 2);
        var displayName = parts.Length == 2 ? parts[0] : string.Empty;
        var path = parts.Length == 2 ? parts[1] : entry;
        var fileName = Path.GetFileName(path);

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            NativeMethods.RemoveFontResourceEx(path, NativeMethods.FR_DEFAULT, IntPtr.Zero);

        RemoveFromRegistry(displayName, fileName, RegistryView.Registry64);
        RemoveFromRegistry(displayName, fileName, RegistryView.Registry32);

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"WARN 已卸载 {fileName}，但文件删除失败: {ex.Message}";
            }
        }

        return $"OK 已卸载 {fileName}";
    }

    /// <summary>清理坏字体：只删除注册表项，不触碰文件（文件本身已不存在）。</summary>
    public static string RemoveBrokenCore(string entry)
    {
        var parts = entry.Split('|', 2);
        var displayName = parts.Length == 2 ? parts[0] : string.Empty;
        var fileName = Path.GetFileName(parts.Length == 2 ? parts[1] : entry);

        var removed = RemoveFromRegistry(displayName, fileName, RegistryView.Registry64)
                      | RemoveFromRegistry(displayName, fileName, RegistryView.Registry32);

        return removed
            ? $"OK 已清理无效字体项 {displayName} ({fileName})"
            : $"WARN 未找到注册表项: {displayName} ({fileName})";
    }

    private static bool RegisterInRegistry(string valueName, string fileName, RegistryView view)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = baseKey.CreateSubKey(FontsKeyPath, writable: true);
            if (key is null) return false;
            key.SetValue(valueName, fileName, RegistryValueKind.String);

            var typeName = Path.GetExtension(fileName).Equals(".ttc", StringComparison.OrdinalIgnoreCase)
                ? "(TrueType collection)"
                : "(TrueType)";
            key.SetValue($"{Path.GetFileNameWithoutExtension(fileName)} {typeName}", fileName, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool RemoveFromRegistry(string displayName, string fileName, RegistryView view)
    {
        var removed = false;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
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

    /// <summary>普通权限下的安装入口：拉起带 UAC 的自身副本完成安装。</summary>
    public static Task<ElevationResult> InstallAsync(IEnumerable<string> paths, CancellationToken token = default) =>
        ElevationHost.RunElevatedAsync("--install-font", paths, token);

    /// <summary>普通权限下的卸载入口。</summary>
    public static Task<ElevationResult> UninstallAsync(IEnumerable<string> entries, CancellationToken token = default) =>
        ElevationHost.RunElevatedAsync("--uninstall-font", entries, token);

    /// <summary>普通权限下的坏字体清理入口。</summary>
    public static Task<ElevationResult> CleanBrokenAsync(IEnumerable<string> entries, CancellationToken token = default) =>
        ElevationHost.RunElevatedAsync("--clean-broken", entries, token);
}
