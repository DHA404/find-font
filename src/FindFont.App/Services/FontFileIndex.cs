using System.Collections.Concurrent;
using System.IO;
using FindFont.App.Helpers;

namespace FindFont.App.Services;

/// <summary>
/// 按「文件」维度并行预解析字体元数据。
/// <para>
/// 扫描链路的瓶颈不在算，而在系统调用：逐项处理时同一个文件会被打开、读文件头、
/// 读 name 表好几遍，字体目录又是冷缓存。先把去重后的文件清单一次性并行探完，
/// 后续拼装列表就只剩纯内存操作。
/// </para>
/// </summary>
internal static class FontFileIndex
{
    /// <summary>单个文件的探测结果。<see cref="Faces"/> 为空表示读不出元数据（如旧式 .fon）。</summary>
    internal sealed record Probe(IReadOnlyList<FontFileInfo> Faces, long Size, DateTime Modified)
    {
        /// <summary>该文件包含的字面数；读不出元数据时按 1 计（旧式 .fon 能正常显示）。</summary>
        internal int FaceCount => Faces.Count == 0 ? 1 : Faces.Count;
    }

    /// <summary>
    /// 并行探测一批文件。返回的字典键为完整路径（大小写不敏感），
    /// 单个文件的失败被隔离在内部，不会中断整轮。
    /// </summary>
    internal static Dictionary<string, Probe> Build(IReadOnlyList<string> paths)
    {
        var result = new Dictionary<string, Probe>(paths.Count, StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0) return result;

        var bag = new ConcurrentDictionary<string, Probe>(StringComparer.OrdinalIgnoreCase);
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount),
        };

        Parallel.ForEach(paths, options, path => bag[path] = ProbeFile(path));

        foreach (var pair in bag) result[pair.Key] = pair.Value;
        return result;
    }

    /// <summary>探测单个文件：一次打开读完所有字面，顺带取大小与修改时间。</summary>
    internal static Probe ProbeFile(string path)
    {
        IReadOnlyList<FontFileInfo> faces;
        try
        {
            faces = FontMetadataReader.ReadFile(path);
        }
        catch (Exception ex) when (FontMetadataReader.IsRecoverable(ex))
        {
            faces = Array.Empty<FontFileInfo>();
        }

        var (size, modified) = ReadStamp(path);
        return new Probe(faces, size, modified);
    }

    private static (long Size, DateTime Modified) ReadStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return (0, DateTime.MinValue);
            return (info.Length, info.LastWriteTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (0, DateTime.MinValue);
        }
    }
}
