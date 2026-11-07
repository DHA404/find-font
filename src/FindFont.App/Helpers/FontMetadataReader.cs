using System.Buffers.Binary;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FindFont.App.Helpers;

/// <summary>从字体文件中读取的元数据。</summary>
public sealed record FontFileInfo(
    string FamilyName,
    string ChineseFamilyName,
    string FaceName,
    string ChineseFaceName,
    int Weight,
    bool IsItalic)
{
    /// <summary>该字体在容器内的序号（TTC 里从 0 开始），单字体恒为 0。</summary>
    public int FaceIndex { get; init; }

    /// <summary>容器里的字体总数，单字体恒为 1。</summary>
    public int FaceCount { get; init; } = 1;

    /// <summary>OS/2 fsType 原始位域；未读到 OS/2 表时为 -1。</summary>
    public int EmbeddingFlags { get; init; } = -1;

    /// <summary>由字符覆盖位推断的语言；推断不出来时为 null。</summary>
    public Models.FontLanguage? ScriptLanguage { get; init; }

    /// <summary>可变字体轴描述（如 "wght 100–900"）；不是可变字体时为空。</summary>
    public string VariationAxes { get; init; } = string.Empty;
}

/// <summary>
/// 字体文件元数据读取器。
/// <para>
/// 主路径是直接解析 OpenType/TrueType 的 <c>name</c> / <c>OS/2</c> / <c>head</c> 表：
/// 只读几十 KB 的表头，约 0.4ms/文件。实测 <see cref="GlyphTypeface"/> 每次构造约 43ms
/// （底层 DirectWrite 会完整解析字形），因此仅作为解析失败时的回退路径。
/// </para>
/// <para>
/// 两条路径都不碰 <see cref="FontFamily"/> 的全局资源缓存，不会触发计划书 3AB 的内存泄漏。
/// </para>
/// </summary>
public static partial class FontMetadataReader
{
    /// <summary>读取字体文件元数据；无法解析（坏字体/非字体文件）时返回 null。多成员容器只取第一个。</summary>
    public static FontFileInfo? TryRead(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        try
        {
            if (!File.Exists(filePath)) return null;

            var head = TryReadSfnt(filePath);
            if (head is not null) return head;

            return TryReadWithGlyphTypeface(filePath);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// 读出容器里的每个字体（TTC/OTC 的每个成员）。单字体返回单元素列表；
    /// 完全解析不出时退回第一个可读字面。解析失败的成员被跳过。
    /// </summary>
    public static IReadOnlyList<FontFileInfo> ReadAllFaces(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return Array.Empty<FontFileInfo>();

        try
        {
            if (!File.Exists(filePath)) return Array.Empty<FontFileInfo>();

            long[]? offsets;
            using (var stream = OpenStream(filePath))
            {
                offsets = SfntReader.ReadFaceOffsets(stream);
            }

            if (offsets is null || offsets.Length <= 1)
            {
                var single = TryRead(filePath);
                return single is null ? Array.Empty<FontFileInfo>() : new[] { single };
            }

            var faces = new List<FontFileInfo>(offsets.Length);
            for (var i = 0; i < offsets.Length; i++)
            {
                var info = TryReadSfnt(filePath, i);
                if (info is null) continue;

                faces.Add(info with
                {
                    FaceIndex = i,
                    FaceCount = offsets.Length,
                    // 同一容器里各成员的家族名往往相同，序号是唯一区分手段。
                    FaceName = string.IsNullOrWhiteSpace(info.FaceName)
                        ? $"Face {i + 1}"
                        : info.FaceName,
                });
            }

            if (faces.Count > 0) return faces;

            var fallback = TryRead(filePath);
            return fallback is null ? Array.Empty<FontFileInfo>() : new[] { fallback };
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return Array.Empty<FontFileInfo>();
        }
    }

    /// <summary>仅走直读 sfnt 的路径，供测试断言「没有退回 GlyphTypeface」。</summary>
    internal static FontFileInfo? TryReadSfnt(string filePath) => TryReadSfnt(filePath, 0);

    /// <summary>
    /// 读容器里有几个字面（只读文件头，不解析 name 表，微秒级）。
    /// 单字体文件、读不出来都返回 1。
    /// </summary>
    public static int CountFaces(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return 1;
        try
        {
            if (!File.Exists(filePath)) return 1;

            using var stream = OpenStream(filePath);
            var offsets = SfntReader.ReadFaceOffsets(stream);
            if (offsets is null or { Length: 0 }) return 1;
            return offsets.Length;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return 1;
        }
    }

    /// <summary>
    /// 读取指定字面的元数据（公开入口）。<paramref name="faceIndex"/> 超出范围时
    /// 退回首字面，再不行退回 <see cref="TryRead"/> 的完整回退链。
    /// </summary>
    public static FontFileInfo? TryReadFace(string filePath, int faceIndex)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;

        try
        {
            if (!File.Exists(filePath)) return null;

            var info = TryReadSfnt(filePath, faceIndex);
            if (info is not null) return info;

            if (faceIndex > 0) return null;
            return TryRead(filePath);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return null;
        }
    }

    /// <summary>直读 sfnt 指定字面（TTC 成员序号）。</summary>
    internal static FontFileInfo? TryReadSfnt(string filePath, int faceIndex)
    {
        using var stream = OpenStream(filePath);

        var offsets = SfntReader.ReadFaceOffsets(stream);
        if (offsets is null) return null;

        return ReadFaceCore(stream, offsets, faceIndex);
    }


    /// <summary>OS/2 fsType 的位定义（OpenType 规范）。</summary>
    private const int FsTypeRestricted = 0x0002;      // bit 1：仅限已授权用途
    private const int FsTypePreviewPrint = 0x0004;    // bit 2：只读嵌入
    private const int FsTypeEditable = 0x0008;        // bit 3：可编辑嵌入
    private const int FsTypeNoSubsetting = 0x0100;    // bit 8：禁止子集化
    private const int FsTypeBitmapOnly = 0x0200;      // bit 9：只允许位图嵌入

    /// <summary>把 fsType 位域翻译成给用户看的一句话。</summary>
    /// <remarks>
    /// 位权限里只有 bit1 是「法律意义上的禁止」；bit2/bit3 说的是允许怎么用，
    /// 不是允许不允许装。所以界面文案区分「禁止嵌入」与「可安装」，不把两者混成一句。
    /// </remarks>
    public static string DescribeEmbedding(int flags)
    {
        if (flags < 0) return "未知（缺少 OS/2 表）";

        var license =
            (flags & FsTypeRestricted) != 0 ? "禁止嵌入" :
            (flags & FsTypeEditable) != 0 ? "可安装（允许嵌入）" :
            (flags & FsTypePreviewPrint) != 0 ? "仅可预览与打印" :
            (flags & FsTypeBitmapOnly) != 0 ? "仅位图嵌入" :
            "无嵌入限制";

        // bit8 与许可位不冲突，是可以并列的附加限制；但已经「禁止嵌入」时
        // 再谈子集化没有意义，反而让读者以为还有别的用法可商量。
        if ((flags & FsTypeNoSubsetting) != 0
            && (flags & FsTypeRestricted) == 0
            && (flags & FsTypeBitmapOnly) == 0)
        {
            license += "，不允许子集化";
        }

        return license;
    }

    /// <summary>该字体的许可位是否声明「禁止嵌入」（bit1）。</summary>
    public static bool IsEmbeddingRestricted(int flags) =>
        flags >= 0 && (flags & FsTypeRestricted) != 0;

    internal static bool IsRecoverable(Exception ex) =>
        ex is FileFormatException or NotSupportedException or ArgumentException
            or IOException or UnauthorizedAccessException or UriFormatException
            or System.Runtime.InteropServices.COMException
            or IndexOutOfRangeException or OverflowException;

    private static FileStream OpenStream(string filePath) =>
        new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);

    internal static bool ContainsCjk(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var ch in text)
        {
            if (ch is >= '\u4E00' and <= '\u9FFF') return true;
            if (ch is >= '\u3400' and <= '\u4DBF') return true;
            if (ch is >= '\u3040' and <= '\u30FF') return true;
            if (ch is >= '\uAC00' and <= '\uD7AF') return true;
        }
        return false;
    }
}
