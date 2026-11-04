using System.Buffers.Binary;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FindFont.App.Helpers;

/// <summary>从字体文件中读取的元数据。</summary>
public sealed record FontFileInfo(
    string FamilyName,
    string ChineseFamilyName,
    string FaceName,
    string ChineseFaceName,
    int Weight,
    bool IsItalic);

/// <summary>
/// 字体文件元数据读取器。
/// <para>
/// 主路径是直接解析 OpenType/TrueType 的 <c>name</c> / <c>OS/2</c> / <c>head</c> 表：
/// 只读几十 KB 的表头，约 1ms/文件。实测 <see cref="GlyphTypeface"/> 每次构造约 43ms
/// （底层 DirectWrite 会完整解析字形），2256 个字体要 90 秒以上，且完全不走并行，
/// 因此仅作为解析失败时的回退路径。
/// </para>
/// <para>
/// 两条路径都不碰 <see cref="FontFamily"/> 的全局资源缓存，不会触发计划书 3AB 的内存泄漏。
/// </para>
/// </summary>
public static class FontMetadataReader
{
    /// <summary>读取字体文件元数据；无法解析（坏字体/非字体文件）时返回 null。</summary>
    public static FontFileInfo? TryRead(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        try
        {
            if (!File.Exists(filePath)) return null;

            var head = TryReadSfnt(filePath);
            if (head is not null) return head;

            return TryReadWithGlyphTypeface(filePath);
        }
        catch (Exception ex) when (ex is FileFormatException or NotSupportedException or ArgumentException
                                       or IOException or UnauthorizedAccessException or UriFormatException
                                       or System.Runtime.InteropServices.COMException
                                       or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>仅走直读 sfnt 的路径，供测试断言「没有退回 GlyphTypeface」。</summary>
    internal static FontFileInfo? TryReadSfnt(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, 4096, FileOptions.RandomAccess);

        var tables = SfntReader.ReadTables(stream);
        if (tables is null) return null;

        var names = NameTableParser.ParseNameRecords(tables.NameTable);
        if (names.Count == 0) return null;

        var family = NameTableParser.PickFamily(names, NameTableParser.NameIdTypographicFamily)
                     ?? NameTableParser.PickFamily(names, NameTableParser.NameIdFamily);
        if (family is null) return null;

        var subfamily = NameTableParser.PickSubfamily(names, NameTableParser.NameIdTypographicSubfamily)
                        ?? NameTableParser.PickSubfamily(names, NameTableParser.NameIdSubfamily);

        var chineseFamily = family.Value.Chinese ?? string.Empty;
        var englishFamily = family.Value.English ?? family.Value.Raw ?? string.Empty;

        if (string.IsNullOrWhiteSpace(englishFamily) && string.IsNullOrWhiteSpace(chineseFamily ))
            return null;

        var weight = 400;
        var italic = false;

        if (SfntReader.ReadOs2Table(stream, tables) is { } os2)
        {
            weight = (int)BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(4));
            if (os2.Length >= 64)
            {
                var fsSelection = BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(62));
                italic = (fsSelection & 0x01) != 0; // ITALIC
            }
        }

        if (!italic && SfntReader.ReadHeadTable(stream, tables) is { } head)
        {
            var macStyle = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(44));
            italic = (macStyle & 0x02) != 0; // ITALIC
        }

        if (weight is < 1 or > 1000) weight = 400;

        var faceEnglish = subfamily?.English ?? subfamily?.Raw ?? string.Empty;
        var faceChinese = subfamily?.Chinese ?? string.Empty;

        return new FontFileInfo(
            FamilyName: string.IsNullOrWhiteSpace(englishFamily) ? chineseFamily : englishFamily,
            ChineseFamilyName: chineseFamily,
            FaceName: faceEnglish,
            ChineseFaceName: faceChinese,
            Weight: weight,
            IsItalic: italic);
    }

    // ---------------------------------------------------- GlyphTypeface 回退

    private static FontFileInfo? TryReadWithGlyphTypeface(string filePath)
    {
        var uri = new Uri(Path.GetFullPath(filePath));
        var glyph = new GlyphTypeface(uri);

        var familyEnglish = GetName(glyph.FamilyNames, CultureInfo.GetCultureInfo("en-US"))
                            ?? glyph.FamilyNames.Values.FirstOrDefault()
                            ?? string.Empty;
        var familyChinese = FirstChinese(glyph.FamilyNames);

        var faceEnglish = GetName(glyph.FaceNames, CultureInfo.GetCultureInfo("en-US"))
                          ?? glyph.FaceNames.Values.FirstOrDefault()
                          ?? string.Empty;
        var faceChinese = FirstChinese(glyph.FaceNames);

        if (string.IsNullOrWhiteSpace(familyEnglish) && string.IsNullOrWhiteSpace(familyChinese))
            return null;

        return new FontFileInfo(
            FamilyName: string.IsNullOrWhiteSpace(familyEnglish) ? familyChinese! : familyEnglish,
            ChineseFamilyName: familyChinese ?? string.Empty,
            FaceName: faceEnglish,
            ChineseFaceName: faceChinese ?? string.Empty,
            Weight: glyph.Weight.ToOpenTypeWeight(),
            IsItalic: glyph.Style == FontStyles.Italic || glyph.Style == FontStyles.Oblique);
    }

    private static string? GetName(IDictionary<CultureInfo, string> names, CultureInfo culture)
    {
        if (names.Count == 0) return null;
        if (names.TryGetValue(culture, out var direct) && !string.IsNullOrWhiteSpace(direct)) return direct;

        foreach (var (key, value) in names)
        {
            if (string.Equals(key.TwoLetterISOLanguageName, culture.TwoLetterISOLanguageName,
                    StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }

    private static string? FirstChinese(IDictionary<CultureInfo, string> names)
    {
        foreach (var (key, value) in names)
        {
            if (key.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        foreach (var value in names.Values)
        {
            if (ContainsCjk(value)) return value;
        }
        return null;
    }

    internal static bool ContainsCjk(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var ch in text)
        {
            if (ch is >= '\u4E00' and <= '\u9FFF') return true;
            if (ch is >= '\u3400' and <= '\u4DBF') return true;
            if (ch is >= '\u3040' and <= '\u30FF') return true;
            if (ch is >= '\uAC00' and <= '\uD7AF') return true;
        }
        return false;
    }
}
