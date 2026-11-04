using Microsoft.Win32;
using FindFont.App.Helpers;

namespace FindFont.App.Services;

/// <summary>安装 / 卸载 / 坏字体清理的底层实现（在提权进程中执行，也可直接调用）。</summary>
public static partial class FontInstaller
{
    private const string FontsKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";

    /// <summary>
    /// 可安装的扩展名。只有 sfnt 容器能被 GDI / DirectWrite 读取；
    /// WOFF/WOFF2 是网页字体（压缩包 + 变换表），装进字体目录后系统根本不认，
    /// 会做出「装了但看不见」的假成功，所以明确排除。
    /// </summary>
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".ttf", ".otf", ".ttc", ".otc" };

    /// <summary>用户可能误当成字体拖进来的网页字体扩展名，用于给出明确提示。</summary>
    private static readonly HashSet<string> WebFontExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".woff", ".woff2", ".eot" };

    public static bool IsSupportedFontFile(string path) =>
        !string.IsNullOrWhiteSpace(path) && SupportedExtensions.Contains(Path.GetExtension(path));

    public static bool IsWebFontFile(string path) =>
        !string.IsNullOrWhiteSpace(path) && WebFontExtensions.Contains(Path.GetExtension(path));

    /// <summary>安装被接受、或给出为什么不能安装的具体原因。</summary>
    public static string DescribeInstallRefusal(string path) =>
        IsWebFontFile(path)
            ? $"{Path.GetFileName(path)} 是网页字体（WOFF），Windows 字体系统无法安装。请先转换成 .ttf 或 .otf。"
            : $"{Path.GetFileName(path)} 不是受支持的字体格式（仅支持 .ttf / .otf / .ttc / .otc）。";

    // ------------------------------------------------------------ 安装台账

    /// <summary>
    /// 本工具装过的字体文件清单。
    /// 卸载时只有「台账里有记录」的文件才允许删除——系统自带字体、用户手工丢进
    /// Fonts 目录的字体都不在台账里，因此永远不会被本程序删掉。
    /// </summary>
    private static string InstalledRecordPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "find-font", "installed.txt");

    /// <summary>该文件是否由本工具安装（决定卸载时能否删文件）。</summary>
    public static bool IsManagedInstall(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var record = InstalledRecordPath;
            if (!File.Exists(record)) return false;

            var full = Path.GetFullPath(path);
            foreach (var line in File.ReadAllLines(record))
            {
                if (line.Length == 0) continue;
                if (string.Equals(line, full, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 台账读不到时就当成「不是我们装的」——保守方向，宁可留着文件。
        }
        return false;
    }

    private static void RecordInstalled(string path)
    {
        try
        {
            var record = InstalledRecordPath;
            Directory.CreateDirectory(Path.GetDirectoryName(record)!);
            var line = Path.GetFullPath(path);
            if (IsManagedInstall(line)) return;
            File.AppendAllLines(record, new[] { line });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 记账失败不影响安装本身，只是卸载时不会删文件。
        }
    }

    private static void ForgetInstalled(string path)
    {
        try
        {
            var record = InstalledRecordPath;
            if (!File.Exists(record)) return;

            var full = Path.GetFullPath(path);
            var kept = File.ReadAllLines(record)
                .Where(l => l.Length > 0 && !string.Equals(l, full, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            File.WriteAllLines(record, kept);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    // ---------------------------------------------------------------- 安装

    /// <summary>安装单个字体文件到系统字体目录并注册（需要管理员权限）。</summary>
    public static string InstallCore(string sourcePath)
    {
        if (!File.Exists(sourcePath)) return $"SKIP {sourcePath}: 文件不存在";
        if (!IsSupportedFontFile(sourcePath)) return $"FAIL {DescribeInstallRefusal(sourcePath)}";

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
        RegisterInRegistry(fileName, RegistryView.Registry64);
        RegisterInRegistry(fileName, RegistryView.Registry32);
        RecordInstalled(target);

        return added > 0
            ? $"OK 已安装 {fileName}"
            : $"WARN {fileName}: 已复制但系统未能加载该字体（可能是坏字体）";
    }

    // ---------------------------------------------------------------- 卸载

    /// <summary>卸载字体：移除注册表项；只有本工具安装的文件才会删（进回收站）。</summary>
    public static string UninstallCore(string entry)
    {
        // entry 可以是 "显示名|完整路径" 或单独的路径。
        var parts = entry.Split('|', 2);
        var displayName = parts.Length == 2 ? parts[0] : string.Empty;
        var path = parts.Length == 2 ? parts[1] : entry;
        var fileName = Path.GetFileName(path);

        var exists = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        if (exists) NativeMethods.RemoveFontResourceEx(path, NativeMethods.FR_DEFAULT, IntPtr.Zero);

        RemoveFromRegistry(displayName, fileName, RegistryView.Registry64);
        RemoveFromRegistry(displayName, fileName, RegistryView.Registry32);

        if (!exists) return $"OK 已卸载 {fileName}（文件已不存在，只清了登记）";

        // 关键保护：台账里没有记录 = 系统自带字体或用户自己放进来的文件。
        // 这种情况下只清登记、保留文件，绝不 File.Delete。
        if (!IsManagedInstall(path))
            return $"OK 已清除 {fileName} 的登记，并保留字体文件（该文件不是本工具安装的）";

        if (!TryDeleteToRecycleBin(path, out var error))
            return $"WARN 已清除 {fileName} 的登记，但文件删除失败: {error}";

        ForgetInstalled(path);
        return $"OK 已卸载 {fileName}（文件已移入回收站）";
    }

    private static bool TryDeleteToRecycleBin(string path, out string error)
    {
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // 回收站不可用（网络盘、配额、权限）时不退化成硬删除——留文件总比删错好。
            error = ex.Message;
            return false;
        }
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
    // ------------------------------------------------------------ 提权入口

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
