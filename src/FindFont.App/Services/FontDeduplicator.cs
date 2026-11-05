using FindFont.App.Models;

namespace FindFont.App.Services;

/// <summary>
/// 扫描结果的去重容器。
/// <para>
/// 同一份字体数据会被登记多次：注册表的 64/32 两个视图各有一份同名项，
/// 用户字体目录里又躺着一批注册表已登记过的文件。按「家族名 + 样式名」去重
/// 抓不住这些重复（用户目录那条没有样式名），于是改用文件路径做主键。
/// </para>
/// <para>
/// 自 0.5.0 起 .ttc/.otc 会展开成多个字面，同一路径因此可能合法地对应多行，
/// 所以主键是「路径 + 字面序号」。
/// </para>
/// </summary>
internal sealed class FontDeduplicator
{
    private readonly Dictionary<string, List<FontItem>> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _byFace = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>去重后的结果列表，顺序与加入顺序一致。</summary>
    internal List<FontItem> Items { get; } = new(512);

    /// <summary>该文件路径是否已有条目（用于跳过用户字体目录里注册表已收录的文件）。</summary>
    internal bool ContainsPath(string fullPath) => _byPath.ContainsKey(fullPath);

    private static string KeyOf(FontItem item) =>
        item.FullPath.Length > 0 ? item.FullPath : "\0missing\0" + item.FileName;

    private static string FaceKeyOf(FontItem item) =>
        KeyOf(item) + "\u0002" + item.FaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal void Add(FontItem item)
    {
        var key = KeyOf(item);

        if (!_byPath.TryGetValue(key, out var bucket))
        {
            _byPath[key] = bucket = new List<FontItem>(1);
            bucket.Add(item);
            _byFace.Add(FaceKeyOf(item));
            Items.Add(item);
            return;
        }

        // 路径相同不等于字面相同：.ttc/.otc 一个文件里可能装着多个家族，
        // 注册表也可能用不同名字登记同一文件的多个字面。同一个字面序号只能出现一次；
        // 序号相同但家族名不同（注册表登记名与真实家族名不一致）时保留已有项并补全信息。
        if (_byFace.Add(FaceKeyOf(item)))
        {
            bucket.Add(item);
            Items.Add(item);
            return;
        }

        foreach (var existing in bucket)
        {
            if (existing.FaceIndex != item.FaceIndex) continue;

            // 同名字面：只补全信息，不新增行。
            if (existing.StyleName.Length == 0 && item.StyleName.Length > 0)
            {
                existing.StyleName = item.StyleName;
            }
            return;
        }
    }
}
