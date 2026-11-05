using System.Buffers.Binary;
using FindFont.App.Models;

namespace FindFont.App.Helpers;

/// <summary>
/// 用少量探测码位量一个字体字面的字形覆盖：码位在不在 cmap 里、以及命中后的字形轮廓是否为空。
/// 只做定向随机读（head / maxp / cmap 子表头 / loca / glyf），不整表解析 ——
/// 本机字体的 cmap 合计几十 MB，整表读会把首屏拖垮。
/// </summary>
internal static class GlyphCoverageReader
{
    /// <summary>
    /// 量一个文件里某个字面的覆盖情况。任何异常都吞掉并返回 Unknown，绝不抛出。
    /// </summary>
    internal static GlyphCoverage Measure(string filePath, int faceIndex, IReadOnlyList<int> codepoints)
    {
        if (codepoints.Count == 0) return GlyphCoverage.Unknown(true, 0);

        try
        {
            using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);

            var offsets = SfntReader.ReadFaceOffsets(stream);
            if (offsets is null || faceIndex < 0 || faceIndex >= offsets.Length)
                return GlyphCoverage.Unknown(true, codepoints.Count);

            var tables = SfntReader.ReadTables(stream, offsets[faceIndex]);
            if (tables is null || tables.CmapOffset < 0)
                return GlyphCoverage.Unknown(true, codepoints.Count);

            var head = SfntReader.ReadHeadTable(stream, tables);
            var maxp = SfntReader.ReadMaxpTable(stream, tables);
            var numGlyphs = maxp is null ? 0 : BinaryPrimitives.ReadUInt16BigEndian(maxp.AsSpan(4));

            var canCheckGlyphs = tables.LocaOffset >= 0 && tables.GlyfOffset >= 0 &&
                                 head is not null && numGlyphs > 0;

            var cmap = CmapReader.Open(stream, tables.CmapOffset, tables.CmapLength);

            var missing = new List<char>();
            var empty = new List<char>();
            var probed = 0;

            foreach (var codepoint in codepoints)
            {
                if (codepoint < 0 || codepoint > 0x10FFFF) continue;
                probed++;

                var gid = cmap?.Lookup(codepoint) ?? 0;
                if (gid == 0)
                {
                    missing.Add(Describe(codepoint));
                    continue;
                }

                if (!canCheckGlyphs) continue;

                if (head is not null && IsEmptyGlyph(stream, tables, head, numGlyphs, gid))
                    empty.Add(Describe(codepoint));
            }

            var kind = BuildKind(missing.Count, empty.Count);
            return new GlyphCoverage(
                kind,
                probed,
                missing.Count,
                empty.Count,
                missing,
                empty,
                canCheckGlyphs);
        }
        catch (Exception ex) when (FontMetadataReader.IsRecoverable(ex))
        {
            return GlyphCoverage.Unknown(true, codepoints.Count);
        }
    }

    private static GlyphCoverageKind BuildKind(int missing, int empty) => (missing > 0, empty > 0) switch
    {
        (true, true) => GlyphCoverageKind.Mixed,
        (true, false) => GlyphCoverageKind.MissingFromMap,
        (false, true) => GlyphCoverageKind.EmptyGlyph,
        _ => GlyphCoverageKind.Complete,
    };

    private static char Describe(int codepoint) =>
        codepoint is >= 0 and <= 0xFFFF ? (char)codepoint : '?';

    /// <summary>
    /// glyf 轮廓是否为空：先看 loca 的区间长度为 0（真·空），否则读 glyf 开头的
    /// numberOfContours。== 0 是空轮廓，&lt; 0 是组合字形（有内容）。
    /// </summary>
    private static bool IsEmptyGlyph(
        Stream stream, SfntReader.SfntTables tables, byte[] head, int numGlyphs, int gid)
    {
        if (gid < 0 || gid >= numGlyphs || gid + 1 > numGlyphs) return false;

        var longFormat = BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(50)) != 0;

        var start = ReadLoca(stream, tables, gid, longFormat);
        var end = ReadLoca(stream, tables, gid + 1, longFormat);
        if (start < 0 || end < 0) return false;
        if (end <= start) return true;

        if (end - start < 2) return true;

        Span<byte> shapeHeader = stackalloc byte[2];
        stream.Position = tables.GlyfOffset + start;
        if (stream.Read(shapeHeader) < 2) return false;

        return BinaryPrimitives.ReadInt16BigEndian(shapeHeader) == 0;
    }

    private static long ReadLoca(Stream stream, SfntReader.SfntTables tables, int index, bool longFormat)
    {
        var position = (long)index * (longFormat ? 4 : 2);
        if (position < 0 || position >= tables.LocaLength) return -1;

        stream.Position = tables.LocaOffset + position;
        if (longFormat)
        {
            Span<byte> buf = stackalloc byte[4];
            return stream.Read(buf) < 4 ? -1 : BinaryPrimitives.ReadUInt32BigEndian(buf);
        }

        Span<byte> shortBuf = stackalloc byte[2];
        return stream.Read(shortBuf) < 2 ? -1 : BinaryPrimitives.ReadUInt16BigEndian(shortBuf) * 2L;
    }
}
