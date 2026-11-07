using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FindFont.App.Helpers;

/// <summary>
/// 解析 fvar（可变字体坐标轴）表，把轴列表压成一行给用户看的文字。
/// <para>
/// 只读轴，不读命名实例：用户真正要判断的是「这个字体能调什么、范围多大」，
/// 实例名（Thin / Light / 特粗…）在列表里塞不下，也容易和样式名混淆。
/// </para>
/// </summary>
internal static class VariationAxesParser
{
    private const int HeaderSize = 16;
    private const int AxisRecordMinSize = 20;

    /// <summary>把 fvar 表翻译成 <c>wght 100–900 · wdth 75–100</c> 这样的字符串；没有轴时返回空串。</summary>
    internal static string Describe(byte[]? fvar)
    {
        if (fvar is null || fvar.Length < HeaderSize) return string.Empty;

        var axesArrayOffset = BinaryPrimitives.ReadUInt16BigEndian(fvar.AsSpan(4));
        var axisCount = BinaryPrimitives.ReadUInt16BigEndian(fvar.AsSpan(8));
        var axisSize = BinaryPrimitives.ReadUInt16BigEndian(fvar.AsSpan(10));

        if (axisCount == 0) return string.Empty;
        if (axisSize < AxisRecordMinSize) return string.Empty;

        var start = axesArrayOffset;
        var end = (long)start + (long)axisCount * axisSize;
        if (start < HeaderSize || start > fvar.Length || end > fvar.Length) return string.Empty;

        var builder = new StringBuilder(axisCount * 16);
        var shown = 0;

        for (var i = 0; i < axisCount; i++)
        {
            var at = start + i * axisSize;
            var tag = ReadTag(fvar, at);
            if (tag.Length == 0) continue;

            var min = ReadFixed(fvar, at + 4);
            var max = ReadFixed(fvar, at + 12);
            if (min is null || max is null) continue;

            if (shown > 0) builder.Append(" · ");
            builder.Append(tag);
            builder.Append(' ');
            builder.Append(min.Value == max.Value
                ? Format(min.Value)
                : Format(min.Value) + "–" + Format(max.Value));

            shown++;

            // 轴再多也没人看，四根就够说明问题了。
            if (shown == 4)
            {
                if (axisCount > shown) builder.Append(" …");
                break;
            }
        }

        return shown == 0 ? string.Empty : builder.ToString();
    }

    /// <summary>轴标签是四个 ASCII 字符（wght / wdth / ital…）。</summary>
    private static string ReadTag(byte[] data, int offset)
    {
        if (offset + 4 > data.Length) return string.Empty;

        Span<char> chars = stackalloc char[4];
        for (var i = 0; i < 4; i++)
        {
            var b = data[offset + i];
            if (b < 0x20 || b > 0x7E) return string.Empty;
            chars[i] = (char)b;
        }

        return new string(chars).Trim();
    }

    /// <summary>16.16 定点数转 double。越界返回 null，避免野值渲染成天文数字。</summary>
    private static double? ReadFixed(byte[] data, int offset)
    {
        if (offset + 4 > data.Length) return null;

        var raw = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
        var value = raw / 65536.0;
        return value is < -1_000_000 or > 1_000_000 ? null : value;
    }

    private static string Format(double value)
    {
        var rounded = Math.Round(value, 2);
        return Math.Abs(rounded - Math.Round(rounded)) < 0.005
            ? ((long)Math.Round(rounded)).ToString(CultureInfo.InvariantCulture)
            : rounded.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
