using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FindFont.App.Helpers;

/// <summary>
/// GlyphTypeface 回退路径。只在这台机器的 sfnt 直读失败时才走，
/// 每次约 43ms —— 所以单独放一个文件，提醒读代码的人这条路径很贵。
/// </summary>
public static partial class FontMetadataReader
{
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
}
