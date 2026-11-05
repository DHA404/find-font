using System.IO;
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
/// 字体扫描器（计划书 2C 混合方案）。
/// <para>
/// 0.6.0 起改为三段流水：并行读注册表 → 解析并缓存文件路径 → 按「唯一文件」并行预解析元数据。
/// 这样一次扫描就把家族名、字重、语言、嵌入权限全部拿到，后台补名阶段基本没有活干。
/// </para>
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

    private static readonly string[] FontExtensions = { "*.ttf", "*.otf", "*.ttc", "*.otc" };

    /// <summary>同步扫描注册表与用户字体目录，返回带完整元数据的字体列表。</summary>
    public FontScanResult Scan()
    {
        var started = DateTime.UtcNow;
        var entries = FontRegistryReader.ReadEntries();

        // 路径解析结果按「注册表里的值」缓存：HKLM 的 64/32 视图各有一份同名登记，
        // 不缓存的话同一个文件名要白跑好几遍 File.Exists。
        var pathCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var probes = BuildProbes(entries, pathCache);

        var dedup = new FontDeduplicator();
        var missingFiles = 0;
        var parseFailures = 0;

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Value)) continue;

            var path = Lookup(entry.Value, pathCache);
            if (path is null)
            {
                missingFiles++;
                dedup.Add(FontItemBuilder.CreateBroken(entry.Name, entry.Value, entry.IsSystem, "字体文件缺失"));
                continue;
            }

            var probe = probes.TryGetValue(path, out var found) ? found : FontFileIndex.ProbeFile(path);
            parseFailures += FontItemBuilder.Add(dedup, probe, path, entry.Name, entry.IsSystem);
        }

        // 用户级字体目录里可能还有注册表未登记的字体。
        // 这里能拿到的只有文件名，不保证和注册表里的家族名一致，
        // 所以按名字去重合不上——已经被注册表收录的文件直接按路径跳过。
        var userFontDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Fonts");
        if (Directory.Exists(userFontDir))
        {
            var unregistered = SafeEnumerate(userFontDir, FontExtensions)
                .Where(file => !dedup.ContainsPath(file))
                .ToList();

            foreach (var pair in FontFileIndex.Build(unregistered))
            {
                parseFailures += FontItemBuilder.Add(dedup, pair.Value, pair.Key, null, isSystem: false);
            }
        }

        var items = dedup.Items;
        items.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));

        return new FontScanResult(
            items,
            entries.Count,
            missingFiles,
            parseFailures,
            DateTime.UtcNow - started);
    }

    /// <summary>
    /// 后台补全：扫描阶段已补齐绝大部分信息，这里只处理仍然缺名字或标记异常的项。
    /// </summary>
    public async Task EnhanceAsync(
        IEnumerable<FontItem> items,
        Action<FontItem>? onUpdated,
        IProgress<int>? progress = null,
        CancellationToken token = default)
    {
        var pending = items
            .Where(item => string.IsNullOrWhiteSpace(item.LocalizedName) || item.IsBroken)
            .ToList();

        if (pending.Count == 0)
        {
            progress?.Report(0);
            return;
        }

        var done = 0;
        var ui = SynchronizationContext.Current;

        await Task.Run(() =>
        {
            var batch = new List<FontItem>(64);
            foreach (var item in pending)
            {
                token.ThrowIfCancellationRequested();

                if (!string.IsNullOrWhiteSpace(item.FullPath) && File.Exists(item.FullPath))
                {
                    var info = FontMetadataReader.ReadFile(item.FullPath)
                        .FirstOrDefault(f => f.FaceIndex == item.FaceIndex);

                    if (info is null)
                    {
                        if (FontItemBuilder.IsLegacyBitmapFont(item.FullPath))
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
                        FontItemBuilder.ApplyMetadata(item, info);
                    }
                }
                else
                {
                    item.IsBroken = true;
                    item.StatusMessage = "字体文件缺失";
                }

                batch.Add(item);
                done++;

                // 逐项跨线程回调在几千条时会淹没 UI 线程，每 50 项批量回填一次。
                if (batch.Count >= 50) Flush(batch, onUpdated, ui);
                progress?.Report(done);
            }

            Flush(batch, onUpdated, ui);
        }, token).ConfigureAwait(false);
    }

    private static void Flush(List<FontItem> batch, Action<FontItem>? onUpdated, SynchronizationContext? ui)
    {
        if (batch.Count == 0) return;

        if (onUpdated is null || ui is null)
        {
            foreach (var item in batch) onUpdated?.Invoke(item);
        }
        else
        {
            var copy = batch.ToArray();
            ui.Post(_ =>
            {
                foreach (var item in copy) onUpdated(item);
            }, null);
        }

        batch.Clear();
    }

    private static Dictionary<string, FontFileIndex.Probe> BuildProbes(
        IReadOnlyList<FontRegistryEntry> entries,
        Dictionary<string, string?> pathCache)
    {
        var unique = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Value)) continue;

            var path = Lookup(entry.Value, pathCache);
            if (path is null) continue;
            if (seen.Add(path)) unique.Add(path);
        }

        return FontFileIndex.Build(unique);
    }

    /// <summary>解析路径并记入缓存；同一个注册表值只做一次文件系统探测。</summary>
    private static string? Lookup(string value, Dictionary<string, string?> cache)
    {
        if (cache.TryGetValue(value, out var cached)) return cached;

        var path = ResolvePath(value);
        cache[value] = path;
        return path;
    }

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
}
