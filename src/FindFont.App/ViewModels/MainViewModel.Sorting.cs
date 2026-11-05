using FindFont.App.Models;

namespace FindFont.App.ViewModels;

/// <summary>
/// 结果集排序。排序只作用于已筛选出的列表副本，不动 <c>_allFonts</c> 的原始顺序，
/// 这样随时切回「按名称」都能得到稳定的基准序。
/// </summary>
public partial class MainViewModel
{
    private static readonly IComparer<FontItem> ByName = Comparer<FontItem>.Create(static (a, b) =>
        string.Compare(a.BestName, b.BestName, StringComparison.CurrentCultureIgnoreCase));

    private static readonly IComparer<FontItem> ByRecent = Comparer<FontItem>.Create(static (a, b) =>
    {
        var result = b.ModifiedAt.CompareTo(a.ModifiedAt);
        return result != 0 ? result : ByName.Compare(a, b);
    });

    private static readonly IComparer<FontItem> BySize = Comparer<FontItem>.Create(static (a, b) =>
    {
        var result = b.FileSize.CompareTo(a.FileSize);
        return result != 0 ? result : ByName.Compare(a, b);
    });

    private static readonly IComparer<FontItem> ByLanguage = Comparer<FontItem>.Create(static (a, b) =>
    {
        var result = ((int)a.Language).CompareTo((int)b.Language);
        return result != 0 ? result : ByName.Compare(a, b);
    });

    private FontSortMode CurrentSortMode => ((int)SelectedSortIndex) switch
    {
        1 => FontSortMode.Recent,
        2 => FontSortMode.Size,
        3 => FontSortMode.Language,
        _ => FontSortMode.Name,
    };

    private IComparer<FontItem> CurrentComparer => CurrentSortMode switch
    {
        FontSortMode.Recent => ByRecent,
        FontSortMode.Size => BySize,
        FontSortMode.Language => ByLanguage,
        _ => ByName,
    };

    /// <summary>供测试直接验证排序规则，不依赖界面。</summary>
    internal static void SortItems(List<FontItem> items, FontSortMode mode)
    {
        var comparer = mode switch
        {
            FontSortMode.Recent => ByRecent,
            FontSortMode.Size => BySize,
            FontSortMode.Language => ByLanguage,
            _ => ByName,
        };
        items.Sort(comparer);
    }
}
