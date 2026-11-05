using System.Buffers.Binary;

namespace FindFont.App.Helpers;

/// <summary>
/// cmap 子表查找。整个 cmap 表一次读进内存（本机字体平均 36 KB，最大约 2 MB），
/// 之后按码位在内存里二分，不再触碰磁盘。
/// </summary>
internal sealed class CmapReader
{
    private readonly byte[] _data;
    private readonly int _subtable;
    private readonly int _format;
    private readonly int _segmentCount;

    private CmapReader(byte[] data, int subtable, int format, int segmentCount)
    {
        _data = data;
        _subtable = subtable;
        _format = format;
        _segmentCount = segmentCount;
    }

    /// <summary>读出 cmap 并挑选最合适的一张子表；不可用时返回 null。</summary>
    internal static CmapReader? Open(Stream stream, long offset, int length)
    {
        if (offset < 0 || length < 20 || length > 16 * 1024 * 1024) return null;

        stream.Position = offset;
        var data = new byte[length];
        var read = stream.Read(data, 0, length);
        if (read < 20) return null;
        if (read != length) Array.Resize(ref data, read);

        var count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
        if (count == 0 || count > 256) return null;

        var best = -1;
        var bestScore = int.MinValue;
        var bestFormat = 0;

        for (var i = 0; i < count; i++)
        {
            var entry = 4 + i * 8;
            if (entry + 8 > data.Length) break;

            var platform = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(entry));
            var encoding = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(entry + 2));
            var subOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entry + 4));
            if (subOffset < 0 || subOffset + 6 > data.Length) continue;

            var format = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(subOffset));
            var score = Score(platform, encoding, format);
            if (score <= bestScore) continue;

            best = subOffset;
            bestScore = score;
            bestFormat = format;
        }

        if (best < 0) return null;

        var segments = bestFormat == 4 ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(best + 6)) / 2 : 0;
        return new CmapReader(data, best, bestFormat, segments);
    }

    /// <summary>
    /// 子表优先级：Unicode 全平面 (3,10)/(0,6) fmt12 最高，然后是 BMP 的 (3,1)/(0,x) fmt4，
    /// 再往后是符号表与 fmt6/fmt0。分数相同时保留先出现的子表。
    /// </summary>
    private static int Score(int platform, int encoding, int format)
    {
        var baseScore = format switch
        {
            12 => 100,
            4 => 60,
            6 => 20,
            0 => 10,
            _ => int.MinValue,
        };
        if (baseScore == int.MinValue) return int.MinValue;

        if (platform == 3 && encoding == 10) return baseScore + 40;
        if (platform == 0 && encoding is 4 or 6) return baseScore + 35;
        if (platform == 3 && encoding == 1) return baseScore + 30;
        if (platform == 0) return baseScore + 25;
        if (platform == 3 && encoding == 0) return baseScore + 10;
        return baseScore;
    }

    /// <summary>码位到字形号；没有映射时返回 0（.notdef）。</summary>
    internal int Lookup(int codepoint) => _format switch
    {
        4 => LookupFormat4(codepoint),
        12 => LookupFormat12(codepoint),
        6 => LookupFormat6(codepoint),
        0 => LookupFormat0(codepoint),
        _ => 0,
    };

    private int LookupFormat0(int codepoint) =>
        codepoint is >= 0 and < 256 && _subtable + 6 + codepoint < _data.Length
            ? _data[_subtable + 6 + codepoint]
            : 0;

    private int LookupFormat6(int codepoint)
    {
        var first = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(_subtable + 6));
        var count = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(_subtable + 8));
        var index = codepoint - first;
        if (index < 0 || index >= count) return 0;

        var position = _subtable + 10 + index * 2;
        return position + 2 > _data.Length ? 0 : BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(position));
    }

    /// <summary>fmt4：查 segments 定段落，再按 idRangeOffset 的两种分支算字形号。</summary>
    private int LookupFormat4(int codepoint)
    {
        if (codepoint > 0xFFFF || _segmentCount == 0) return 0;

        var endsBase = _subtable + 14;
        var startsBase = endsBase + _segmentCount * 2 + 2;
        var deltasBase = startsBase + _segmentCount * 2;
        var rangeBase = deltasBase + _segmentCount * 2;

        if (rangeBase + _segmentCount * 2 > _data.Length) return 0;

        // 段末数组是升序的，二分找到第一个 end >= codepoint 的段。
        var low = 0;
        var high = _segmentCount - 1;
        var segment = -1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var end = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(endsBase + mid * 2));
            if (end < codepoint)
            {
                low = mid + 1;
            }
            else
            {
                segment = mid;
                high = mid - 1;
            }
        }

        if (segment < 0) return 0;

        var start = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(startsBase + segment * 2));
        if (codepoint < start) return 0;

        var delta = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(deltasBase + segment * 2));
        var rangeOffset = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(rangeBase + segment * 2));

        if (rangeOffset == 0) return (codepoint + delta) & 0xFFFF;

        var glyphPosition = rangeBase + segment * 2 + rangeOffset + (codepoint - start) * 2;
        if (glyphPosition < 0 || glyphPosition + 2 > _data.Length) return 0;

        var glyph = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(glyphPosition));
        return glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
    }

    /// <summary>fmt12：二分分组，组内线性偏移。</summary>
    private int LookupFormat12(int codepoint)
    {
        var groupCount = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_subtable + 12));
        if (groupCount == 0 || groupCount > 100_000) return 0;

        var low = 0u;
        var high = groupCount - 1;
        var found = long.MaxValue;

        while (low <= high)
        {
            var mid = (low + high) / 2;
            var entry = _subtable + 16 + (int)mid * 12;
            if (entry + 12 > _data.Length) return 0;

            var start = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(entry));
            var end = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(entry + 4));
            var glyphStart = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(entry + 8));

            if (codepoint < start)
            {
                if (mid == 0) break;
                high = mid - 1;
            }
            else if (codepoint > end)
            {
                low = mid + 1;
            }
            else
            {
                found = glyphStart + (uint)(codepoint - start);
                break;
            }
        }

        return found is <= 0 or > int.MaxValue ? 0 : (int)found;
    }
}
