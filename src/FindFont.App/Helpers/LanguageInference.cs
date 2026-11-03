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
