using System.Buffers.Binary;
using System.Globalization;
using System.Text;
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
    private const uint NameTableTag = 0x6E616D65; // 'name'
    private const uint Os2TableTag = 0x4F532F32;  // 'OS/2'
    private const uint HeadTableTag = 0x68656164; // 'head'

    // name 表中我们关心的 nameID。
    private const int NameIdFamily = 1;
    private const int NameIdSubfamily = 2;
    private const int NameIdTypographicFamily = 16;
    private const int NameIdTypographicSubfamily = 17;

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

    // ---------------------------------------------------------------- sfnt

    private static FontFileInfo? TryReadSfnt(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, 4096, FileOptions.RandomAccess);

        var length = stream.Length;
        if (length < 12) return null;

        // TTC（.ttc/.otc）以 ttcf 开头，后接字体偏移表；只取第一个字体。
        Span<byte> header = stackalloc byte[12];
        stream.Position = 0;
        if (stream.Read(header) < 12) return null;

        var tag = BinaryPrimitives.ReadUInt32BigEndian(header);
        long sfntOffset = 0;

        if (tag == 0x74746366) // 'ttcf'
        {
            Span<byte> countBuf = stackalloc byte[4];
            stream.Position = 8;
            if (stream.Read(countBuf) < 4) return null;
            var numFonts = BinaryPrimitives.ReadUInt32BigEndian(countBuf);
            if (numFonts == 0) return null;

            Span<byte> offBuf = stackalloc byte[4];
            stream.Position = 12;
            if (stream.Read(offBuf) < 4) return null;
            sfntOffset = BinaryPrimitives.ReadUInt32BigEndian(offBuf);

            stream.Position = sfntOffset;
            if (stream.Read(header) < 12) return null;
            tag = BinaryPrimitives.ReadUInt32BigEndian(header);
        }

        // 0x00010000 (TrueType)、'OTTO' (CFF)、'true'、'typ1'
        if (tag is not (0x00010000 or 0x4F54544F or 0x74727565 or 0x74797031)) return null;

        var numTables = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        if (numTables == 0 || numTables > 512) return null;

        var dir = new byte[numTables * 16];
        if (stream.Read(dir) < dir.Length) return null;

        long nameOffset = -1, nameLength = -1;
        long os2Offset = -1, os2Length = -1;
        long headOffset = -1;

        for (var i = 0; i < numTables; i++)
        {
            var entry = dir.AsSpan(i * 16, 16);
            var tableTag = BinaryPrimitives.ReadUInt32BigEndian(entry);
            var offset = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            var len = BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
            if (offset > length || len > length) continue;

            switch (tableTag)
            {
                case NameTableTag:
                    nameOffset = offset; nameLength = len; break;
                case Os2TableTag:
                    os2Offset = offset; os2Length = len; break;
                case HeadTableTag:
                    headOffset = offset; break;
            }
        }

        if (nameOffset < 0 || nameLength < 6 || nameLength > 4 * 1024 * 1024) return null;

        var nameData = new byte[nameLength];
        stream.Position = nameOffset;
        if (stream.Read(nameData) < nameData.Length) return null;

        var names = ParseNameRecords(nameData);
        if (names.Count == 0) return null;

        var family = PickFamily(names, NameIdTypographicFamily) ?? PickFamily(names, NameIdFamily);
        if (family is null) return null;

        var subfamily = PickSubfamily(names, NameIdTypographicSubfamily) ?? PickSubfamily(names, NameIdSubfamily);

        var chineseFamily = family.Value.Chinese ?? string.Empty;
        var englishFamily = family.Value.English ?? family.Value.Raw ?? string.Empty;

        if (string.IsNullOrWhiteSpace(englishFamily) && string.IsNullOrWhiteSpace(chineseFamily ))
            return null;

        var weight = 400;
        var italic = false;

        if (os2Offset >= 0 && os2Length >= 6)
        {
            var buf = new byte[Math.Min(os2Length, 78)];
            stream.Position = os2Offset;
            var read = stream.Read(buf, 0, buf.Length);
            if (read >= 6)
            {
                weight = (int)BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(4));
                if (read >= 64)
                {
                    var fsSelection = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(62));
                    italic = (fsSelection & 0x01) != 0; // ITALIC
                }
            }
        }

        if (!italic && headOffset >= 0)
        {
            var buf = new byte[54];
            stream.Position = headOffset;
            if (stream.Read(buf, 0, buf.Length) >= 54)
            {
                var macStyle = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(44));
                italic = (macStyle & 0x02) != 0; // ITALIC
            }
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

    private sealed record NameCandidate(string? English, string? Chinese, string? Raw, int Score);

    private static List<(int NameId, NameCandidate Candidate)> ParseNameRecords(byte[] data)
    {
        var result = new List<(int, NameCandidate)>();
        var span = data.AsSpan();

        var count = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
        var stringOffset = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);
        if (count == 0 || stringOffset > data.Length) return result;

        for (var i = 0; i < count; i++)
        {
            var rec = 6 + i * 12;
            if (rec + 12 > data.Length) break;

            var platformId = BinaryPrimitives.ReadUInt16BigEndian(span[rec..]);
            var encodingId = BinaryPrimitives.ReadUInt16BigEndian(span[(rec + 2)..]);
            var languageId = BinaryPrimitives.ReadUInt16BigEndian(span[(rec + 4)..]);
            var nameId = BinaryPrimitives.ReadUInt16BigEndian(span[(rec + 6)..]);
            var strLen = BinaryPrimitives.ReadUInt16BigEndian(span[(rec + 8)..]);
            var strOff = BinaryPrimitives.ReadUInt16BigEndian(span[(rec + 10)..]);

            if (nameId is not (NameIdFamily or NameIdSubfamily or NameIdTypographicFamily or NameIdTypographicSubfamily))
                continue;

            var start = stringOffset + strOff;
            if (start < 0 || start + strLen > data.Length || strLen == 0) continue;

            var raw = span.Slice(start, strLen);
            string? text = null;
            var isUtf16 = platformId is 0 or 3;

            try
            {
                text = isUtf16
                    ? Encoding.BigEndianUnicode.GetString(raw)
                    : Encoding.Latin1.GetString(raw);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(text)) continue;
            text = text.Trim('\0', ' ', '\uFFFD').Trim();
            if (text.Length == 0) continue;

            // 只有真正含 CJK 的字符串才算「中文本地化名」：某些简体字体把
            // 中文语言槽位填成 'Regular' 这类拉丁文字，不能当作中文名展示。
            var isChinese = ContainsCjk(text);
            var isEnglish = platformId == 3 && languageId == 0x0409
                            || platformId == 1 && languageId == 0
                            || platformId == 0 && encodingId <= 1;

            // 评分：Windows 英文 Unicode 最高，mac 英文次之，再是任意含 CJK 的条目。
            var score = (platformId, languageId) switch
            {
                (3, 0x0409) => 100,
                (1, 0) => 80,
                (0, _) => 70,
                _ => isChinese || IsChineseLanguage(platformId, languageId) ? 60 : 10,
            };

            result.Add((nameId, new NameCandidate(isEnglish ? text : null, isChinese ? text : null, text, score)));
        }

        return result;
    }

    private static bool IsChineseLanguage(int platformId, int languageId)
        => platformId == 3 && languageId is 0x0804 or 0x0404 or 0x0C04 or 0x1404 or 0x1004
           || platformId == 1 && languageId is >= 19 and <= 23; // legacy zh

    private static (string? English, string? Chinese, string? Raw)? PickFamily(
        List<(int NameId, NameCandidate Candidate)> names, int nameId, string? preferredChinese = null)
    {
        var candidates = names.Where(n => n.NameId == nameId).Select(n => n.Candidate).ToList();
        if (candidates.Count == 0) return null;

        var best = candidates.OrderByDescending(c => c.Score).First();
        var english = candidates
            .Select(c => c.English)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var chinese = candidates
            .Select(c => c.Chinese)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        return (english, chinese, best.Raw);
    }

    private static (string? English, string? Chinese, string? Raw)? PickSubfamily(
        List<(int NameId, NameCandidate Candidate)> names, int nameId)
        => PickFamily(names, nameId);

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
