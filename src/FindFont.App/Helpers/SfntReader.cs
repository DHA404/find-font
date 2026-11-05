using System.Buffers.Binary;

namespace FindFont.App.Helpers;

/// <summary>
/// sfnt 容器读取：12 字节文件头、TTC 偏移表、表目录，以及 name / OS/2 / head 三张表的位置。
/// 这里只负责「把字节取出来」，不做任何语义解释。
/// </summary>
internal static class SfntReader
{
    private const uint NameTableTag = 0x6E616D65; // 'name'
    private const uint Os2TableTag = 0x4F532F32;  // 'OS/2'
    private const uint HeadTableTag = 0x68656164; // 'head'
    private const uint TtcTag = 0x74746366;       // 'ttcf'

    /// <summary>表目录读出的位置信息；name 表内容已读出，OS/2 与 head 只记位置，按需再读。</summary>
    internal sealed record SfntTables(byte[] NameTable, long Os2Offset, int Os2Length, long HeadOffset);

    /// <summary>
    /// 读出容器里每个字体（TTC 成员）的 sfnt 起始偏移。普通字体返回单元素数组，
    /// 不是可识别容器时返回 null。只读文件头与偏移表，几十字节。
    /// </summary>
    internal static long[]? ReadFaceOffsets(Stream stream)
    {
        var length = stream.Length;
        if (length < 12) return null;

        Span<byte> header = stackalloc byte[12];
        stream.Position = 0;
        if (stream.Read(header) < 12) return null;

        var tag = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (tag != TtcTag) return new[] { 0L };

        // ttcf: 4 字节 tag + 4 字节 version + 4 字节 numFonts + 每成员 4 字节偏移。
        var numFonts = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        if (numFonts is 0 or > 1024) return null;
        if (length < 12 + numFonts * 4) return null;

        var buf = new byte[numFonts * 4];
        stream.Position = 12;
        if (stream.Read(buf, 0, buf.Length) < buf.Length) return null;

        var offsets = new long[numFonts];
        for (var i = 0; i < numFonts; i++)
        {
            offsets[i] = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(i * 4));
        }

        return offsets;
    }

    /// <summary>读取指定字体的表目录与 name 表字节；不是可识别的 sfnt 时返回 null。</summary>
    internal static SfntTables? ReadTables(Stream stream, long sfntOffset = 0)
    {
        var length = stream.Length;
        if (sfntOffset < 0 || length < sfntOffset + 12) return null;

        Span<byte> header = stackalloc byte[12];
        stream.Position = sfntOffset;
        if (stream.Read(header) < 12) return null;

        var tag = BinaryPrimitives.ReadUInt32BigEndian(header);

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

        return new SfntTables(
            nameData,
            os2Offset,
            os2Offset >= 0 ? (int)Math.Min(os2Length, 78) : 0,
            headOffset);
    }

    /// <summary>读取 OS/2 表前 78 字节；表不存在或长度不足 6 字节时返回 null。</summary>
    internal static byte[]? ReadOs2Table(Stream stream, SfntTables tables)
    {
        if (tables.Os2Offset < 0 || tables.Os2Length < 6) return null;

        var buf = new byte[tables.Os2Length];
        stream.Position = tables.Os2Offset;
        var read = stream.Read(buf, 0, buf.Length);
        if (read < 6) return null;

        return read == buf.Length ? buf : buf.AsSpan(0, read).ToArray();
    }

    /// <summary>读取 head 表前 54 字节；表不存在或读不满时返回 null。</summary>
    internal static byte[]? ReadHeadTable(Stream stream, SfntTables tables)
    {
        if (tables.HeadOffset < 0) return null;

        var buf = new byte[54];
        stream.Position = tables.HeadOffset;
        return stream.Read(buf, 0, buf.Length) >= 54 ? buf : null;
    }
}
