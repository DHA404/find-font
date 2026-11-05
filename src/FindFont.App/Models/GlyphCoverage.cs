using System.Globalization;
using System.Text;

namespace FindFont.App.Models;

/// <summary>字形覆盖的判定结果分类。</summary>
public enum GlyphCoverageKind
{
    Unknown = 0,
    Complete = 1,
    MissingFromMap = 2,
    EmptyGlyph = 3,
    Mixed = 4,
}

/// <summary>
/// 一组探测码位在某个字体字面里的覆盖情况。
/// 两种缺字必须分开报：字体里根本没有这个码位（DirectWrite 只能回退或画 .notdef），
/// 与码位有映射但字形轮廓为空（画出来就是空白）。后者才是「看起来字体坏了」的元凶。
/// </summary>
public sealed record GlyphCoverage(
    GlyphCoverageKind Kind,
    int Probed,
    int MissingFromCmap,
    int EmptyGlyph,
    IReadOnlyList<char> MissingChars,
    IReadOnlyList<char> EmptyChars,
    bool EmptyGlyphSupported)
{
    /// <summary>完全没有探测数据时的占位。</summary>
    public static GlyphCoverage Unknown(bool emptyGlyphSupported, int probed) => new(
        GlyphCoverageKind.Unknown, probed, 0, 0, Array.Empty<char>(), Array.Empty<char>(), emptyGlyphSupported);

    public bool HasHoles => MissingFromCmap > 0 || EmptyGlyph > 0;

    public bool HasMissingFromCmap => MissingFromCmap > 0;

    public bool HasEmptyGlyph => EmptyGlyph > 0;

    /// <summary>徽标文案，短到能塞进一行元数据。</summary>
    public string Label => Kind switch
    {
        GlyphCoverageKind.Unknown => "未检测",
        GlyphCoverageKind.Complete => "字形完整",
        _ => string.Create(CultureInfo.InvariantCulture, $"缺字 {MissingFromCmap + EmptyGlyph}/{Probed}"),
    };

    /// <summary>细分文案，给右栏与 ToolTip 用。</summary>
    public string Detail
    {
        get
        {
            if (Kind == GlyphCoverageKind.Unknown) return "未检测";
            if (Kind == GlyphCoverageKind.Complete) return "字形完整";

            var parts = new List<string>(2);
            if (MissingFromCmap > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"无映射 {MissingFromCmap}"));
            if (EmptyGlyph > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"空字形 {EmptyGlyph}"));
            if (!EmptyGlyphSupported) parts.Add("轮廓格式不支持空字形检测");
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"共探测 {Probed} 字"));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>缺字的示例字符，最多 12 个。</summary>
    public string ExampleChars
    {
        get
        {
            if (!HasHoles) return string.Empty;

            var builder = new StringBuilder();
            var used = 0;
            foreach (var ch in MissingChars)
            {
                if (used++ >= 12) break;
                if (builder.Length > 0) builder.Append(' ');
                builder.Append(ch);
            }

            foreach (var ch in EmptyChars)
            {
                if (used++ >= 12) break;
                if (builder.Length > 0) builder.Append(' ');
                builder.Append(ch);
            }

            return builder.ToString();
        }
    }
}
