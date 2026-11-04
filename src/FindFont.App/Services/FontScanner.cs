using FindFont.App.Helpers;
using FindFont.App.Models;

namespace FindFont.App.Services;

/// <summary>扫描结果统计。</summary>
public sealed record FontScanResult(
    IReadOnlyList<FontItem> Items,
    int RegistryEntries,
    int MissingFiles,
    int ParseFailures,
    TimeSpan Elapsed);

/// <summary>
/// 字体扫描器（计划书 2C 混合方案）：
/// 1) 同步读注册表，毫秒级得到字体文件名与注册名，UI 立即出数据；
/// 2) 后台异步解析字体文件真实家族名/本地化名，完成后逐项回填。
/// </summary>
public sealed class FontScanner
{
    private static readonly string[] KnownFontDirs =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Fonts"),
    };

    /// <summary>同步扫描注册表与用户字体目录，返回可直接展示的粗略列表。</summary>
    public FontScanResult Scan()
    {
        var started = DateTime.UtcNow;
        var dedup = new FontDeduplicator();

        int registryEntries = 0;
        int missingFiles = 0;

        void AddEntry(string registryName, string? fileName, bool isSystem)
        {
            registryEntries++;
            if (string.IsNullOrWhiteSpace(fileName)) return;

            var path = ResolvePath(fileName);
            if (path is null)
            {
                missingFiles++;
                var ghost = CreateBrokenItem(registryName, fileName, isSystem, "字体文件缺失");
                dedup.Add(ghost);
                return;
            }

            var cleanedName = FontRegistryReader.CleanRegistryName(registryName);
            var item = new FontItem
            {
                FamilyName = cleanedName,
                DisplayName = cleanedName,
                FileName = Path.GetFileName(path),
                FullPath = path,
                IsSystemFont = isSystem,
                StyleName = FontRegistryReader.GuessStyleFromName(registryName),
                StatusMessage = string.Empty,
            };
            item.IsBold = item.Weight >= 600;
            item.Language = LanguageInference.Infer(cleanedName, item.FileName);
            item.Category = LanguageInference.ToCategory(item.Language);

            dedup.Add(item);
        }

        FontRegistryReader.ReadAll(AddEntry);

        // 用户级字体目录里可能还有注册表未登记的字体。
        // 注意：这里能拿到的只有文件名，不保证和注册表里的家族名一致，
        // 所以按名字去重合不上——已经被注册表收录的文件直接按路径跳过。
        var userFontDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Fonts");
        if (Directory.Exists(userFontDir))
        {
            foreach (var file in SafeEnumerate(userFontDir, "*.ttf", "*.otf", "*.ttc", "*.otc"))
            {
                if (dedup.ContainsPath(file)) continue;

                var name = Path.GetFileNameWithoutExtension(file);
                var item = new FontItem
                {
                    FamilyName = name,
                    DisplayName = name,
                    FileName = Path.GetFileName(file),
                    FullPath = file,
                    IsSystemFont = false,
                    StatusMessage = string.Empty,
                };
                item.Language = LanguageInference.Infer(name);
                item.Category = LanguageInference.ToCategory(item.Language);
                dedup.Add(item);
            }
        }

        var items = dedup.Items;
        items.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));

        return new FontScanResult(
            items,
            registryEntries,
            missingFiles,
            0,
            DateTime.UtcNow - started);
    }

    /// <summary>
    /// 异步补全字体真实名称。每解析完一个就回调一次（UI 线程由调用方保证）。
    /// </summary>
    public async Task EnhanceAsync(
        IEnumerable<FontItem> items,
        Action<FontItem>? onUpdated,
        IProgress<int>? progress = null,
        CancellationToken token = default)
    {
        var list = items.ToList();
        var done = 0;

        await Task.Run(() =>
        {
            foreach (var item in list)
            {
                token.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(item.FullPath) && File.Exists(item.FullPath))
                {
                    var info = FontMetadataReader.TryRead(item.FullPath);
                    if (info is null)
                    {
                        // 旧式 .fon 位图字体本来就装不进 sfnt 解析器，但它能正常显示，
                        // 不能算「异常字体」，否则状态栏的异常数会被 7 个系统字体污染。
                        if (IsLegacyBitmapFont(item.FullPath))
                        {
                            item.IsBroken = false;
                            item.StatusMessage = "旧式位图字体";
                        }
                        else
                        {
                            item.IsBroken = true;
                            item.StatusMessage = "无法解析字体文件";
                        }
                    }
                    else
                    {
                        item.LocalizedName = info.FamilyName;
                        item.ChineseName = info.ChineseFamilyName;
                        item.DisplayName = string.IsNullOrWhiteSpace(info.ChineseFamilyName)
                            ? info.FamilyName
                            : info.ChineseFamilyName;
                        if (!string.IsNullOrWhiteSpace(info.FaceName) || !string.IsNullOrWhiteSpace(info.ChineseFaceName))
                        {
                            item.StyleName = string.IsNullOrWhiteSpace(info.ChineseFaceName)
                                ? info.FaceName
                                : info.ChineseFaceName;
                        }
                        item.Weight = info.Weight;
                        item.IsBold = info.Weight >= 600;
                        item.IsItalic = info.IsItalic;
                        item.IsBroken = false;
                        item.StatusMessage = string.Empty;

                        var language = LanguageInference.Infer(
                            info.ChineseFamilyName, info.FamilyName, item.FileName);
                        item.Language = language;
                        item.Category = LanguageInference.ToCategory(language);
                    }
                }
                else
                {
                    item.IsBroken = true;
                    item.StatusMessage = "字体文件缺失";
                }

                done++;
                progress?.Report(done);
                onUpdated?.Invoke(item);
            }
        }, token).ConfigureAwait(false);
    }

    /// <summary>旧式 GDI 位图字体（.fon）不是 sfnt 容器，解析不出家族名属于预期行为。</summary>
    private static bool IsLegacyBitmapFont(string path)
        => path.EndsWith(".fon", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> SafeEnumerate(string dir, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                yield break;
            }

            foreach (var file in files) yield return file;
        }
    }

    internal static string? ResolvePath(string fileNameOrValue)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrValue)) return null;

        var raw = fileNameOrValue.Trim().Trim('"');

        // 注册表里偶见 "C:\Windows\Fonts\xxx.ttf" 这种完整路径。
        if (raw.Contains('\\') || raw.Contains(':'))
        {
            if (File.Exists(raw)) return Path.GetFullPath(raw);
        }

        var justName = Path.GetFileName(raw);
        if (string.IsNullOrWhiteSpace(justName)) return null;

        foreach (var dir in KnownFontDirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir, justName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // 非法文件名，忽略该候选目录。
            }
        }

        return null;
    }

    private static FontItem CreateBrokenItem(string registryName, string fileName, bool isSystem, string reason) => new()
    {
        FamilyName = FontRegistryReader.CleanRegistryName(registryName),
        DisplayName = FontRegistryReader.CleanRegistryName(registryName),
        FileName = Path.GetFileName(fileName),
        FullPath = string.Empty,
        IsSystemFont = isSystem,
        IsBroken = true,
        StatusMessage = reason,
        StyleName = FontRegistryReader.GuessStyleFromName(registryName),
    };
}
