using System.Buffers.Binary;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FindFont.App.Helpers;

/// <summary>
/// 字体文件元数据读取器（读取实现部分）：一次打开读出全部字面的完整元数据。
/// </summary>
public static partial class FontMetadataReader
{
    /// <summary>
    /// 一次打开读完整个文件的全部字面。
    /// <para>
    /// 扫描器要给每一条字体补齐家族名/字重/语言/嵌入权限，逐项调 <see cref="TryReadFace"/>
    /// 会让同一个文件被打开、读文件头、读 name 表三遍；这里把三次合并成一次顺序读。
    /// </para>
    /// <para>
    /// 返回的列表按容器内字面序号升序；解析不出的字面被跳过。整个文件都读不出来时
    /// 返回空列表（调用方自行决定是否退回 <see cref="TryRead"/> 的 GlyphTypeface 路径）。
    /// </para>
    /// </summary>
    public static IReadOnlyList<FontFileInfo> ReadFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return Array.Empty<FontFileInfo>();

        try
        {
            using var stream = OpenStream(filePath);

            var offsets = SfntReader.ReadFaceOffsets(stream);
            if (offsets is null or { Length: 0 }) return Array.Empty<FontFileInfo>();

            if (offsets.Length == 1)
            {
                var single = ReadFaceCore(stream, offsets, 0);
                return single is null ? Array.Empty<FontFileInfo>() : new[] { single };
            }

            var faces = new List<FontFileInfo>(offsets.Length);
            for (var i = 0; i < offsets.Length; i++)
            {
                var info = ReadFaceCore(stream, offsets, i);
                if (info is null) continue;

                faces.Add(info with
                {
                    FaceIndex = i,
                    FaceCount = offsets.Length,
                    FaceName = string.IsNullOrWhiteSpace(info.FaceName) ? $"Face {i + 1}" : info.FaceName,
                });
            }

            return faces;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return Array.Empty<FontFileInfo>();
        }
    }

    /// <summary>在已打开的流上解析某个字面。流的位置会被来回移动，调用方不必关心。</summary>
    private static FontFileInfo? ReadFaceCore(Stream stream, long[] offsets, int faceIndex)
    {
        if (faceIndex < 0 || faceIndex >= offsets.Length) return null;

        var tables = SfntReader.ReadTables(stream, offsets[faceIndex]);
        if (tables is null) return null;

        var names = NameTableParser.ParseNameRecords(tables.NameTable);
        if (names.Count == 0) return null;

        var family = NameTableParser.PickFamily(names, NameTableParser.NameIdTypographicFamily)
                     ?? NameTableParser.PickFamily(names, NameTableParser.NameIdFamily);
        if (family is null) return null;

        var subfamily = NameTableParser.PickSubfamily(names, NameTableParser.NameIdTypographicSubfamily)
                        ?? NameTableParser.PickSubfamily(names, NameTableParser.NameIdSubfamily);

        var chineseFamily = family.Value.Chinese ?? string.Empty;
        var englishFamily = family.Value.English ?? family.Value.Raw ?? string.Empty;

        if (string.IsNullOrWhiteSpace(englishFamily) && string.IsNullOrWhiteSpace(chineseFamily))
            return null;

        // 可变字体的轴要在同一根流上读，否则 ReadFile 一次打开的好处就白费了。
        var variationAxes = VariationAxesParser.Describe(SfntReader.ReadFvarTable(stream, tables));

        var weight = 400;
        var italic = false;
        var embeddingFlags = -1;
        Models.FontLanguage? script = null;

        if (SfntReader.ReadOs2Table(stream, tables) is { } os2)
        {
            weight = (int)BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(4));

            if (os2.Length >= 10)
            {
                embeddingFlags = (int)BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(8));
            }

            if (os2.Length >= 58)
            {
                script = LanguageInference.FromUnicodeRange(
                    BinaryPrimitives.ReadUInt32BigEndian(os2.AsSpan(42)),
                    BinaryPrimitives.ReadUInt32BigEndian(os2.AsSpan(46)));
            }

            if (os2.Length >= 64)
            {
                var fsSelection = BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(62));
                italic = (fsSelection & 0x01) != 0; // ITALIC
            }
        }

        if (!italic && SfntReader.ReadHeadTable(stream, tables) is { } head)
        {
            var macStyle = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(44));
            italic = (macStyle & 0x02) != 0; // ITALIC
        }

        if (weight is < 1 or > 1000) weight = 400;

        var faceEnglish = subfamily?.English ?? subfamily?.Raw ?? string.Empty;
        var faceChinese = subfamily?.Chinese ?? string.Empty;

        return new FontFileInfo(
            FamilyName: string.IsNullOrWhiteSpace(englishFamily) ? chineseFamily : englishFamily,
            ChineseFamilyName: chineseFamily,
            FaceName: faceEnglish,
            ChineseFaceName: faceChinese,
            Weight: weight,
            IsItalic: italic)
        {
            FaceIndex = faceIndex,
            EmbeddingFlags = embeddingFlags,
            ScriptLanguage = script,
            VariationAxes = variationAxes,
        };
    }
}
