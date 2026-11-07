namespace FindFont.App.Models;

/// <summary>
/// 样本列表的一行。多列布局下 ListBox 的每一项是一行若干瓦片，
/// 而不是单个字体 —— 虚拟化以行为单位回收，行内瓦片数量随列数变化。
/// </summary>
public sealed class FontRow
{
    public FontRow(IReadOnlyList<FontItem> items, int columns)
    {
        Items = items;
        Columns = columns < 1 ? 1 : columns;
    }

    /// <summary>这一行包含的字体，从左到右。最后一列未填满时长度小于列数。</summary>
    public IReadOnlyList<FontItem> Items { get; }

    /// <summary>
    /// 该行应占的列数。末行不足时靠它补齐空位，
    /// 否则 UniformGrid 会按实际项数均分，最后一行瓦片比上面的宽一截。
    /// </summary>
    public int Columns { get; }
}
