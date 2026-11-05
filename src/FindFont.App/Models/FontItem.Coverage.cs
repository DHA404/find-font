using CommunityToolkit.Mvvm.ComponentModel;

namespace FindFont.App.Models;

/// <summary>
/// 字形覆盖相关的展示属性。独立一个文件是为了守住「单文件 300 行」的约规，
/// 语义上仍然属于 FontItem。
/// </summary>
public partial class FontItem
{
    /// <summary>当前探测码位下的覆盖结果；未检测时为 null。</summary>
    [ObservableProperty] private GlyphCoverage? _coverage;

    /// <summary>覆盖检测正在排队或进行中。</summary>
    [ObservableProperty] private bool _coveragePending;

    /// <summary>这个字体在当前样品串下有缺字。</summary>
    public bool HasCoverageHoles => Coverage?.HasHoles == true;

    /// <summary>缺「码位映射」，不是空轮廓。</summary>
    public bool HasMissingFromCmap => Coverage?.MissingFromCmap > 0;

    /// <summary>有映射但轮廓为空。</summary>
    public bool HasEmptyGlyph => Coverage?.EmptyGlyph > 0;

    /// <summary>元数据行上的短徽标。</summary>
    public string CoverageLabel => Coverage?.Label ?? (CoveragePending ? "检测中…" : "未检测");

    /// <summary>右栏与 ToolTip 用的细分文案。</summary>
    public string CoverageDetail => Coverage?.Detail ?? (CoveragePending ? "检测中…" : "未检测");

    /// <summary>缺字示例，空格分隔。</summary>
    public string CoverageExamples => Coverage?.ExampleChars ?? string.Empty;

    /// <summary>覆盖结果变化时一次性通知所有派生属性。</summary>
    partial void OnCoverageChanged(GlyphCoverage? value)
    {
        OnPropertyChanged(nameof(HasCoverageHoles));
        OnPropertyChanged(nameof(HasMissingFromCmap));
        OnPropertyChanged(nameof(HasEmptyGlyph));
        OnPropertyChanged(nameof(CoverageLabel));
        OnPropertyChanged(nameof(CoverageDetail));
        OnPropertyChanged(nameof(CoverageExamples));
    }

    partial void OnCoveragePendingChanged(bool value)
    {
        OnPropertyChanged(nameof(CoverageLabel));
        OnPropertyChanged(nameof(CoverageDetail));
    }
}
