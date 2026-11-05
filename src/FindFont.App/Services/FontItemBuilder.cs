using System.IO;
using FindFont.App.Helpers;
using FindFont.App.Models;

namespace FindFont.App.Services;

/// <summary>
/// 扫描结果的拼装：一次文件探测 → 一条或多条 <see cref="FontItem"/>。
/// 从 <see cref="FontScanner"/> 里拆出来，只为守住单文件 300 行的规约。
/// </summary>
internal static class FontItemBuilder
{
    /// <summary>
    /// 把一次探测结果拼成字体行（多字面容器展开成多行）。返回解析失败的次数。
    /// </summary>
    internal static int Add(
        FontDeduplicator dedup,
        FontFileIndex.Probe probe,
        string path,
        string? registryName,
        bool isSystem)
    {
        var fileName = Path.GetFileName(path);
        var baseName = registryName is null
            ? Path.GetFileNameWithoutExtension(path)
            : FontRegistryReader.CleanRegistryName(registryName);
        var registryStyle = registryName is null ? "Regular" : FontRegistryReader.GuessStyleFromName(registryName);

        if (probe.Faces.Count == 0)
        {
            dedup.Add(BuildUnparsed(baseName, fileName, path, registryStyle, probe, isSystem));
            return IsLegacyBitmapFont(path) ? 0 : 1;
        }

        foreach (var info in probe.Faces)
        {
            var item = new FontItem
            {
                FamilyName = baseName,
                DisplayName = baseName,
                FileName = fileName,
                FullPath = path,
                IsSystemFont = isSystem,
                StyleName = registryStyle,
                StatusMessage = string.Empty,
                FaceIndex = info.FaceIndex,
                FaceCount = probe.FaceCount,
                FileSize = probe.Size,
                ModifiedAt = probe.Modified,
            };

            ApplyMetadata(item, info);
            item.Category = LanguageInference.ToCategory(item.Language);
            dedup.Add(item);
        }

        return 0;
    }

    /// <summary>解析不出元数据的文件（旧式 .fon、坏文件）也要有一行可显示。</summary>
    private static FontItem BuildUnparsed(
        string baseName,
        string fileName,
        string path,
        string style,
        FontFileIndex.Probe probe,
        bool isSystem)
    {
        var legacy = IsLegacyBitmapFont(path);
        var item = new FontItem
        {
            FamilyName = baseName,
            DisplayName = baseName,
            FileName = fileName,
            FullPath = path,
            IsSystemFont = isSystem,
            StyleName = style,
            StatusMessage = legacy ? "旧式位图字体" : "无法解析字体文件",
            IsBroken = !legacy,
            FileSize = probe.Size,
            ModifiedAt = probe.Modified,
            Language = LanguageInference.Infer(baseName, fileName),
        };

        item.Category = LanguageInference.ToCategory(item.Language);
        return item;
    }

    /// <summary>把解析出的元数据写进字体行。</summary>
    internal static void ApplyMetadata(FontItem item, FontFileInfo info)
    {
        item.LocalizedName = info.FamilyName;
        item.ChineseName = info.ChineseFamilyName;

        if (!string.IsNullOrWhiteSpace(info.ChineseFamilyName))
        {
            item.DisplayName = info.ChineseFamilyName;
        }
        else if (!string.IsNullOrWhiteSpace(info.FamilyName))
        {
            item.DisplayName = info.FamilyName;
        }

        if (string.IsNullOrWhiteSpace(item.StyleName))
        {
            item.StyleName = string.IsNullOrWhiteSpace(info.ChineseFaceName) ? info.FaceName : info.ChineseFaceName;
        }

        item.Weight = info.Weight;
        item.IsBold = info.Weight >= 600;
        item.IsItalic = info.IsItalic;
        item.IsBroken = false;
        item.StatusMessage = string.Empty;
        item.FaceIndex = info.FaceIndex;
        item.Embedding = FontMetadataReader.DescribeEmbedding(info.EmbeddingFlags);
        item.Language = LanguageInference.InferWithCoverage(
            info.ScriptLanguage, info.ChineseFamilyName, info.FamilyName, item.FileName);
    }

    /// <summary>旧式 GDI 位图字体（.fon）不是 sfnt 容器，解析不出家族名属于预期行为。</summary>
    internal static bool IsLegacyBitmapFont(string path)
        => path.EndsWith(".fon", StringComparison.OrdinalIgnoreCase);

    /// <summary>注册表登记了但文件找不到的「僵尸字体」。</summary>
    internal static FontItem CreateBroken(string registryName, string fileName, bool isSystem, string reason) => new()
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
