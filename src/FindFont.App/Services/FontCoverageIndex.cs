using System.Collections.Concurrent;
using FindFont.App.Helpers;
using FindFont.App.Models;

namespace FindFont.App.Services;

/// <summary>
/// 字形覆盖检测的服务端：把「样品串」翻成探测码位，按 (路径, 字面, 探测集) 缓存结果，
/// 并行量所有字体。第二次用同样的样品串不会重复读盘。
/// 这里只负责算和存，往 FontItem 上写值由视图模型在 UI 线程统一做。
/// </summary>
internal sealed class FontCoverageIndex
{
    private readonly ConcurrentDictionary<string, GlyphCoverage> _cache = new(StringComparer.OrdinalIgnoreCase);

    private int _lastProbeHash;
    private int[] _lastProbe = Array.Empty<int>();

    /// <summary>把样品串翻成去重后的探测码位，保持首次出现顺序。跳过空白。</summary>
    internal static IReadOnlyList<int> ToCodepoints(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<int>();

        var seen = new HashSet<char>();
        var result = new List<int>(text.Length);
        foreach (var ch in text)
        {
            if (ch is '\r' or '\n' or '\t' or ' ') continue;
            if (!seen.Add(ch)) continue;
            result.Add(ch);
        }

        return result;
    }

    /// <summary>稳定指纹：同样的探测集（顺序无关）得到同样的值。</summary>
    internal static int ProbeHash(IReadOnlyList<int> codepoints)
    {
        var hash = 17;
        foreach (var value in codepoints) hash = hash * 31 + value;
        return hash;
    }

    /// <summary>
    /// 并行量一批字体，只处理缓存未命中的。返回本次真正读盘的字体数。
    /// </summary>
    internal int Measure(
        IEnumerable<FontItem> items,
        IReadOnlyList<int> codepoints,
        CancellationToken token,
        IProgress<int>? progress = null)
    {
        if (codepoints.Count == 0) return 0;

        var probeHash = ProbeHash(codepoints);
        _lastProbeHash = probeHash;
        _lastProbe = codepoints as int[] ?? codepoints.ToArray();

        var pending = new List<FontItem>();
        foreach (var item in items)
        {
            if (item.IsBroken || string.IsNullOrEmpty(item.FullPath)) continue;
            if (_cache.ContainsKey(KeyOf(item, probeHash))) continue;
            pending.Add(item);
        }

        if (pending.Count == 0) return 0;

        var done = 0;
        var parallelism = Math.Max(2, Environment.ProcessorCount);

        Parallel.ForEach(
            pending,
            new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = parallelism },
            item =>
            {
                var key = KeyOf(item, probeHash);
                var coverage = GlyphCoverageReader.Measure(item.FullPath, item.FaceIndex, codepoints);
                _cache[key] = coverage;

                var current = Interlocked.Increment(ref done);
                if (current % 64 == 0) progress?.Report(current);
            });

        progress?.Report(pending.Count);
        return pending.Count;
    }

    /// <summary>拿最近一次探测集的缓存结果，没有就返回 null。</summary>
    internal GlyphCoverage? TryGetCached(FontItem item)
    {
        if (_lastProbeHash == 0) return null;
        if (item.IsBroken || string.IsNullOrEmpty(item.FullPath)) return null;
        return _cache.TryGetValue(KeyOf(item, _lastProbeHash), out var cached) ? cached : null;
    }

    /// <summary>当前缓存对应的探测指纹（0 表示还没量过）。</summary>
    internal int CurrentProbeHash => _lastProbeHash;

    /// <summary>最近一次探测的字数，给状态栏用。</summary>
    internal int CurrentProbeCount => _lastProbe.Length;

    internal void Clear() => _cache.Clear();

    private static string KeyOf(FontItem item, int probeHash) =>
        $"{item.FullPath}\u0002{item.FaceIndex}\u0002{probeHash}";
}
