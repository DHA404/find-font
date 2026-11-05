namespace FindFont.App.Helpers;

/// <summary>基于字体家族名 / 文件名的语言支持推断（对应计划书 6A 轻量规则，不解析字符表）。</summary>
public static class LanguageInference
{
    private static readonly string[] ChineseHints =
    {
        "雅黑", "宋", "黑体", "楷", "仿宋", "隶", "幼圆", "等线", "方正", "华文", "华光",
        "思源", "source han", "noto sans sc", "noto serif sc", "noto sans cjk", "noto serif cjk",
        "simsun", "simhei", "simkai", "simfang", "nsimsun", "fangsong", "kaiti", "heiti",
        "microsoft yahei", "microsoft jhenghei", "dengxian", "lisu", "youyuan", "stxihei",
        "stsong", "stkaiti", "stfangsong", "stheiti", "songti", "heiti sc", "pingfang",
        "hanserif", "zhongsong", "fzhei", "fzshu", "fzyao", "fzkai", "fzfang", "fzli",
        "cjk", "gb2312", "gbk", "gb18030",
    };

    private static readonly string[] JapaneseHints =
    {
        "游ゴシック", "游明朝", "ゴシック", "明朝", "meiryo", "msgothic", "msmincho", "ms p",
        "yu gothic", "yu mincho", "hiragino", "noto sans jp", "noto serif jp", "biz ud",
        "takao", "ipaex", "ipag", "ipam",
    };

    private static readonly string[] KoreanHints =
    {
        "malgun", "gulim", "dotum", "batang", "gungsuh", "nanum", "noto sans kr", "noto serif kr",
        "본고딕", "본명조", "맑은",
    };

    private static readonly string[] LatinHints =
    {
        "arial", "calibri", "cambria", "consolas", "corbel", "georgia", "segoe", "tahoma",
        "times", "trebuchet", "verdana", "helvetica", "roboto", "open sans", "lato", "montserrat",
        "inter", "poppins", "fira", "jetbrains", "source code", "dengxian",
    };

    // OS/2 ulUnicodeRange 里与本工具相关的位。
    private const int UnicodeBitCjkPunctuation = 48;  // CJK Symbols and Punctuation
    private const int UnicodeBitHiragana = 49;
    private const int UnicodeBitKatakana = 50;
    private const int UnicodeBitHangulJamo = 28;
    private const int UnicodeBitHangulSyllables = 56;
    private const int UnicodeBitCjkIdeographs = 59;   // CJK Unified Ideographs

    /// <summary>
    /// 用 OS/2 表的 <c>ulUnicodeRange1..4</c> 判定字符覆盖对应的语言。
    /// 这是比家族名关键词更可靠的依据；无相关位时返回 null，由调用方回落到
    /// <see cref="Infer"/> 的名字推断。
    /// </summary>
    public static Models.FontLanguage? FromUnicodeRange(
        uint range1, uint range2, uint range3 = 0, uint range4 = 0)
    {
        static bool Has(uint r1, uint r2, uint r3, uint r4, int bit)
        {
            var value = (bit >> 5) switch
            {
                0 => r1,
                1 => r2,
                2 => r3,
                3 => r4,
                _ => 0u,
            };
            return (value & (1u << (bit & 31))) != 0;
        }

        var hangul = Has(range1, range2, range3, range4, UnicodeBitHangulSyllables)
                     || Has(range1, range2, range3, range4, UnicodeBitHangulJamo);
        var kana = Has(range1, range2, range3, range4, UnicodeBitHiragana)
                   || Has(range1, range2, range3, range4, UnicodeBitKatakana);
        var cjk = Has(range1, range2, range3, range4, UnicodeBitCjkIdeographs)
                  || Has(range1, range2, range3, range4, UnicodeBitCjkPunctuation);

        // 韩文字体通常同时覆盖 CJK 汉字，先判韩文再判假名，最后才是汉字。
        if (hangul && !kana) return Models.FontLanguage.Korean;

        // 只有假名没有汉字 → 纯日文。只有汉字没有假名 → 中日韩共用，按中文算。
        if (kana && !cjk) return Models.FontLanguage.Japanese;
        if (cjk && !kana) return Models.FontLanguage.Chinese;

        // 汉字与假名同时覆盖是中文字体与日文字体的共有特征（SimSun 也含假名字形），
        // 位域无法区分，返回 null 交给名字推断；硬判只会把中文字体误标成日文。
        return null;
    }

    /// <summary>
    /// 推断字体语言。字符覆盖已给出结论时以它为准（比名字关键词可靠），
    /// 否则退回关键词与 CJK 字符检测。
    /// </summary>
    public static Models.FontLanguage InferWithCoverage(Models.FontLanguage? coverage, params string?[] texts)
        => coverage ?? Infer(texts);

    /// <summary>推断字体语言。传入家族名、本地化名、文件名、样式的组合文本。</summary>
    public static Models.FontLanguage Infer(params string?[] texts)
    {
        var haystack = string.Join(' ', texts.Where(t => !string.IsNullOrWhiteSpace(t))).ToLowerInvariant();
        if (haystack.Length == 0) return Models.FontLanguage.Unknown;

        foreach (var hint in KoreanHints)
            if (haystack.Contains(hint, StringComparison.Ordinal)) return Models.FontLanguage.Korean;

        foreach (var hint in JapaneseHints)
            if (haystack.Contains(hint, StringComparison.Ordinal)) return Models.FontLanguage.Japanese;

        foreach (var hint in ChineseHints)
            if (haystack.Contains(hint, StringComparison.Ordinal)) return Models.FontLanguage.Chinese;

        // 名字里直接含 CJK 字符 → 中文系
        if (FontMetadataReader.ContainsCjk(haystack)) return Models.FontLanguage.Chinese;

        foreach (var hint in LatinHints)
            if (haystack.Contains(hint, StringComparison.Ordinal)) return Models.FontLanguage.Latin;

        return Models.FontLanguage.Latin;
    }

    public static Models.FontCategory ToCategory(Models.FontLanguage language) => language switch
    {
        Models.FontLanguage.Chinese or Models.FontLanguage.Japanese or Models.FontLanguage.Korean =>
            Models.FontCategory.Chinese,
        Models.FontLanguage.Latin => Models.FontCategory.Latin,
        _ => Models.FontCategory.Other,
    };

    public static Models.FontLanguage FromCategory(Models.FontCategory category) => category switch
    {
        Models.FontCategory.Chinese => Models.FontLanguage.Chinese,
        Models.FontCategory.Latin => Models.FontLanguage.Latin,
        _ => Models.FontLanguage.Unknown,
    };
}
