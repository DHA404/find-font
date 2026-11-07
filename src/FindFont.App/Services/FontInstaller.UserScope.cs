using FindFont.App.Helpers;
using Microsoft.Win32;

namespace FindFont.App.Services;

/// <summary>
/// 当前用户级安装（不需要管理员权限）。
/// <para>
/// Windows 10 1809 起支持把字体装进 <c>%LOCALAPPDATA%\Microsoft\Windows\Fonts</c> 并登记到
/// <c>HKCU\Software\Microsoft\Windows NT\CurrentVersion\Fonts</c>：只有当前用户能看到这套字体，
/// 但整个过程不需要 UAC。对「我只是想临时看几种字体」的人来说，这是更合适的默认路径。
/// </para>
/// </summary>
public static partial class FontInstaller
{
    private const string UserFontsKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";

    /// <summary>当前用户字体目录；取不到时返回空串。</summary>
    public static string UserFontDirectory
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(local)
                ? string.Empty
                : Path.Combine(local, "Microsoft", "Windows", "Fonts");
        }
    }

    /// <summary>路径是否落在当前用户字体目录下（决定卸载时该清 HKCU 还是 HKLM）。</summary>
    public static bool IsUserScopePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var dir = UserFontDirectory;
        if (dir.Length == 0) return false;

        try
        {
            var full = Path.GetFullPath(path);
            return full.StartsWith(
                Path.GetFullPath(dir) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>读源文件的 fsType 位域；读不到时返回 -1（未知）。</summary>
    private static int ReadEmbeddingFlags(string path)
    {
        try
        {
            var faces = FontMetadataReader.ReadFile(path);
            return faces.Count == 0 ? -1 : faces[0].EmbeddingFlags;
        }
        catch (Exception ex) when (FontMetadataReader.IsRecoverable(ex))
        {
            return -1;
        }
    }

    /// <summary>
    /// 为当前用户安装字体：复制到用户字体目录 + 写 HKCU + 通知系统。
    /// 这条路径不提权，因此可以在主进程里直接跑。
    /// </summary>
    public static string InstallCoreForUser(string sourcePath)
    {
        if (!File.Exists(sourcePath)) return $"SKIP {Path.GetFileName(sourcePath)}: 文件不存在";
        if (!IsSupportedFontFile(sourcePath)) return $"FAIL {DescribeInstallRefusal(sourcePath)}";

        var dir = UserFontDirectory;
        if (dir.Length == 0) return "FAIL 找不到当前用户的字体目录";

        var fileName = Path.GetFileName(sourcePath);
        var target = Path.Combine(dir, fileName);

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"FAIL {fileName}: 无法创建用户字体目录 {ex.Message}";
        }

        if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.Copy(sourcePath, target, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"FAIL {fileName}: 复制失败 {ex.Message}";
            }
        }

        var added = NativeMethods.AddFontResourceEx(target, NativeMethods.FR_DEFAULT, IntPtr.Zero);
        RegisterInRegistry(fileName, RegistryHive.CurrentUser, RegistryView.Default);
        RecordInstalled(target);

        if (added <= 0) return $"WARN {fileName}: 已复制但系统未能加载该字体（可能是坏字体）";

        return FontMetadataReader.IsEmbeddingRestricted(ReadEmbeddingFlags(sourcePath))
            ? $"OK 已为当前用户安装 {fileName}（字体声明禁止嵌入，请确认授权范围）"
            : $"OK 已为当前用户安装 {fileName}";
    }

    /// <summary>当前用户级卸载入口：HKCU 与用户目录都能在普通权限下处理，不必提权。</summary>
    public static Task<ElevationResult> UninstallForUserAsync(
        IEnumerable<string> entries,
        CancellationToken token = default)
    {
        var list = entries.ToList();

        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();

            var report = new System.Text.StringBuilder();
            foreach (var entry in list)
            {
                token.ThrowIfCancellationRequested();
                report.AppendLine(UninstallCore(entry));
            }

            NativeMethods.BroadcastFontChange();
            var text = report.ToString().TrimEnd();
            var failed = text.Contains("FAIL", StringComparison.Ordinal)
                         || text.Contains("WARN", StringComparison.Ordinal);
            return new ElevationResult(!failed, failed ? 1 : 0, text);
        }, token);
    }

    /// <summary>当前用户级安装入口：不开提权进程，直接在后台线程执行。</summary>
    public static Task<ElevationResult> InstallForUserAsync(
        IEnumerable<string> paths,
        CancellationToken token = default)
    {
        var list = paths.ToList();

        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();

            var report = new System.Text.StringBuilder();
            foreach (var path in list)
            {
                token.ThrowIfCancellationRequested();
                report.AppendLine(InstallCoreForUser(path));
            }

            NativeMethods.BroadcastFontChange();
            var text = report.ToString().TrimEnd();
            return new ElevationResult(true, 0, text);
        }, token);
    }
}
