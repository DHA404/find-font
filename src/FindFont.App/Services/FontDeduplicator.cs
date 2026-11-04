using FindFont.App.Models;

namespace FindFont.App.Services;

/// <summary>
/// 扫描结果的去重容器。
/// <para>
/// 同一份字体数据会被登记多次：注册表的 64/32 两个视图各有一份同名项，
/// 用户字体目录里又躺着一批注册表已登记过的文件。按「家族名 + 样式名」去重
/// 抓不住这些重复（用户目录那条没有样式名），于是改用文件路径做主键。
/// </para>
/// </summary>
internal sealed class FontDeduplicator
{
    private readonly Dictionary<string, List<FontItem>> _byPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>去重后的结果列表，顺序与加入顺序一致。</summary>
    internal List<FontItem> Items { get; } = new(512);

    /// <summary>该文件路径是否已有条目（用于跳过用户字体目录里注册表已收录的文件）。</summary>
    internal bool ContainsPath(string fullPath) => _byPath.ContainsKey(fullPath);

    internal void Add(FontItem item)
    {
        // 缺失文件的项没有 FullPath，退化成按文件名归组。
        var key = item.FullPath.Length > 0 ? item.FullPath : "\0missing\0" + item.FileName;

        if (!_byPath.TryGetValue(key, out var bucket))
        {
            _byPath[key] = bucket = new List<FontItem>(1);
            bucket.Add(item);
            Items.Add(item);
            return;
        }

        // 路径相同不等于字面相同：.ttc/.otc 一个文件里可能装着多个家族，
        // 注册表也可能用不同名字登记同一文件的多个字面。所以只有名字一致才合并，
        // 名字不同就当两个独立条目留下。
        foreach (var existing in bucket)
        {
            if (!string.Equals(existing.FamilyName, item.FamilyName, StringComparison.OrdinalIgnoreCase))
                continue;

            // 同名字面：只补全信息，不新增行。
            if (existing.StyleName.Length == 0 && item.StyleName.Length > 0)
            {
                existing.FamilyName = item.FamilyName;
                existing.DisplayName = item.DisplayName;
                existing.StyleName = item.StyleName;
            }
            return;
        }

        bucket.Add(item);
        Items.Add(item);
    }
}
