namespace FindFont.App.Models;

/// <summary>
/// 样本列表的一行。多列布局下 ListBox 的每一项是一行若干瓦片，
/// 而不是单个字体 —— 虚拟化以行为单位回收，行内瓦片数量随列数变化。
/// </summary>
public sealed class FontRow
{
    public FontRow(IReadOnlyList<FontItem> items) => Items = items;

    /// <summary>这一行包含的字体，从左到右。最后一列未填满时长度小于列数。</summary>
    public IReadOnlyList<FontItem> Items { get; }
}
