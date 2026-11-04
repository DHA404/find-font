using System.Buffers.Binary;
using System.Text;

namespace FindFont.App.Helpers;

/// <summary>name 表里的一条候选名称（英文名、中文名、原始文本及其权重分）。</summary>
internal sealed record NameCandidate(string? English, string? Chinese, string? Raw, int Score);

/// <summary>
/// <c>name</c> 表记录解析：把每个「平台 / 语言 / nameID」记录解成 UTF-16BE 或 Latin1 文本，
/// 并按平台与语言打分，供上层挑选家族名与样式名。
/// </summary>
internal static class NameTableParser
{
    // name 表中我们关心的 nameID。
    internal const int NameIdFamily = 1;
    internal const int NameIdSubfamily = 2;
    internal const int NameIdTypographicFamily = 16;
    internal const int NameIdTypographicSubfamily = 17;

    internal static List<(int NameId, NameCandidate Candidate)> ParseNameRecords(byte[] data)
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
            var isChinese = FontMetadataReader.ContainsCjk(text);
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

    internal static bool IsChineseLanguage(int platformId, int languageId)
        => platformId == 3 && languageId is 0x0804 or 0x0404 or 0x0C04 or 0x1404 or 0x1004
           || platformId == 1 && languageId is >= 19 and <= 23; // legacy zh

    internal static (string? English, string? Chinese, string? Raw)? PickFamily(
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

    internal static (string? English, string? Chinese, string? Raw)? PickSubfamily(
        List<(int NameId, NameCandidate Candidate)> names, int nameId)
        => PickFamily(names, nameId);
}
