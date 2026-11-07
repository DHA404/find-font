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

    /// <summary>
    /// 缺字的示例字符，最多 12 个。优先给东亚字符：中文字体缺几个 ASCII
    /// 是常态（我们只用它排中文），把 "The quick bro" 摆在前面会盖住
    /// 真正该看的那个「你」。
    /// </summary>
    public string ExampleChars
    {
        get
        {
            if (!HasHoles) return string.Empty;

            return Compose(MissingChars, EmptyChars);
        }
    }

    private static string Compose(IReadOnlyList<char> missing, IReadOnlyList<char> empty)
    {
        var picked = new List<char>(12);

        void Take(IReadOnlyList<char> source, bool wideOnly)
        {
            foreach (var ch in source)
            {
                if (picked.Count >= 12) return;
                if (wideOnly && ch < '\u2E80') continue;
                if (picked.Contains(ch)) continue;
                picked.Add(ch);
            }
        }

        Take(missing, wideOnly: true);
        Take(empty, wideOnly: true);

        // 一个东亚字符都没有（纯西文字体缺字之类），退回原顺序，别让这行空着。
        if (picked.Count == 0)
        {
            Take(missing, wideOnly: false);
            Take(empty, wideOnly: false);
        }

        var builder = new StringBuilder();
        foreach (var ch in picked)
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(ch);
        }

        return builder.ToString();
    }
}
