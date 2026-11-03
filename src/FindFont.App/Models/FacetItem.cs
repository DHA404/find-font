using CommunityToolkit.Mvvm.ComponentModel;

namespace FindFont.App.Models;

/// <summary>
/// 左栏索引条的一行：分类名 + 该类下的字体数量。
/// Count 是可观察的——索引行原地更新计数，绝不清空集合，
/// 否则 ListBox 会把 SelectedIndex 写回 -1，把分类筛选打乱。
/// </summary>
public sealed partial class FacetItem : ObservableObject
{
    public FacetItem(string name, int count)
    {
        Name = name;
        Count = count;
    }

    public string Name { get; }

    [ObservableProperty] private int _count;

    /// <summary>数量文本，界面直接绑定，避免在 XAML 里做格式化。</summary>
    public string CountText => Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(CountText));
}
