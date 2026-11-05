using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindFont.App.Collections;
using FindFont.App.Models;
using FindFont.App.Services;

namespace FindFont.App.ViewModels;

/// <summary>主窗口视图模型：扫描、搜索防抖、筛选、选中、安装卸载、坏字体清理。</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly List<FontItem> _allFonts = new(1024);
    private readonly List<FontItem> _lastLargeSelected = new(4);
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _enhanceCts;

    private const int SearchDebounceMs = 300;

    /// <summary>展开态大样本的渲染尺寸（像素）。只有被选中的行才按这个尺寸渲染。</summary>
    public const int LargePreviewWidth = 1120;
    public const int LargePreviewHeight = 220;

    /// <summary>首屏预热行数：约等于 1000px 高窗口能同时看到的行数。</summary>
    internal const int PrewarmRows = 12;

    /// <summary>预热位图尺寸，与 SpecimenRowTemplate 里小样本一致。</summary>
    internal const int PrewarmWidth = 1120;

    internal const int PrewarmHeight = 92;

    /// <summary>界面上显示的版本号，来自 version.txt（经程序集元数据带过来）。</summary>
    public string AppVersion => Helpers.AppInfo.Version;

    /// <summary>带 v 前缀，直接放进标题下的计数行。</summary>
    public string AppVersionDisplay => "v" + AppVersion;

    /// <summary>
    /// 每次筛选结果落地后触发。视图用它播放入场动画——视图模型不碰任何 UI 类型，
    /// 也不关心订阅者是不是窗口。
    /// </summary>
    public event EventHandler? FilterApplied;

    public RangeObservableCollection<FontItem> VisibleFonts { get; } = new();

    public ObservableCollection<FontItem> SelectedFonts { get; } = new();

    /// <summary>左栏索引条：分类名 + 该类在「当前其余筛选条件」下的数量。</summary>
    public ObservableCollection<FacetItem> CategoryFacets { get; } = new();

    public IReadOnlyList<string> CategoryOptions { get; } =
        new[] { "全部", "中文", "西文", "其他" };

    public IReadOnlyList<string> StyleOptions { get; } =
        new[] { "全部字重", "常规", "粗体", "斜体" };

    public IReadOnlyList<double> FontSizeOptions { get; } =
        new[] { 20d, 28d, 36d, 48d, 64d, 88d };

    public IReadOnlyList<string> SortOptions { get; } =
        new[] { "按名称", "按最近修改", "按文件大小", "按语言" };

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _previewText = "The quick brown fox 0123 你好，世界";
    [ObservableProperty] private double _previewFontSize = 36;
    [ObservableProperty] private int _selectedCategoryIndex;
    [ObservableProperty] private int _selectedStyleIndex;
    [ObservableProperty] private int _selectedSortIndex;
    [ObservableProperty] private bool _showBrokenOnly;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private double _loadProgress;
    [ObservableProperty] private string _statusText = "准备就绪";
    [ObservableProperty] private FontItem? _focusedFont;

    // 详情面板的结构化字段（替代原来拼成一大段的多行字符串）。
    [ObservableProperty] private string _colophonFamily = string.Empty;
    [ObservableProperty] private string _colophonLocalized = string.Empty;
    [ObservableProperty] private string _colophonStyle = string.Empty;
    [ObservableProperty] private string _colophonWeight = string.Empty;
    [ObservableProperty] private string _colophonFileName = string.Empty;
    [ObservableProperty] private string _colophonPath = string.Empty;
    [ObservableProperty] private string _colophonLanguage = string.Empty;
    [ObservableProperty] private string _colophonStatus = string.Empty;
    [ObservableProperty] private string _colophonFace = string.Empty;
    [ObservableProperty] private string _colophonEmbedding = string.Empty;

    public int TotalCount => _allFonts.Count;
    public int BrokenCount => _allFonts.Count(f => f.IsBroken);

    public bool HasSelection => SelectedFonts.Count > 0;
    public bool CanCompare => SelectedFonts.Count >= 2;
    /// <summary>可卸载 = 单选 + 不是坏项。系统字体也允许（只清登记、保留文件），由命令层给出不同提示。</summary>
    public bool CanUninstall => SelectedFonts.Count == 1 && !SelectedFonts[0].IsBroken;

    /// <summary>对比按钮的可用条件：至少选中 1 个（单字体也允许放大看）。</summary>
    public bool CanCompareOrSingle => SelectedFonts.Count >= 1;

    /// <summary>右栏档案是否可见：没有选中时折叠，只留索引与样本。</summary>
    public bool IsDetailVisible => FocusedFont is not null;

    /// <summary>样本列表为空时的引导文案。</summary>
    public string EmptyStateText => string.IsNullOrWhiteSpace(SearchText)
        ? "这里还没有字体。点「重新扫描」读一遍本机字体注册表。"
        : "没有匹配的字体。换个关键词，或把分类切回「全部」。";

    /// <summary>当前没有任何可见字体（用于显示空状态）。</summary>
    public bool HasNoResults => VisibleFonts.Count == 0;

    // 风格互斥开关：XAML 用四个方块开关表达同一条筛选轴。
    public bool IsStyleAll
    {
        get => SelectedStyleIndex == 0;
        set { if (value) SetStyleIndex(0); }
    }

    public bool IsStyleRegular
    {
        get => SelectedStyleIndex == 1;
        set { if (value) SetStyleIndex(1); }
    }

    public bool IsStyleBold
    {
        get => SelectedStyleIndex == 2;
        set { if (value) SetStyleIndex(2); }
    }

    public bool IsStyleItalic
    {
        get => SelectedStyleIndex == 3;
        set { if (value) SetStyleIndex(3); }
    }

    private void SetStyleIndex(int index)
    {
        SelectedStyleIndex = index;
    }

    private void RaiseStyleFlags()
    {
        OnPropertyChanged(nameof(IsStyleAll));
        OnPropertyChanged(nameof(IsStyleRegular));
        OnPropertyChanged(nameof(IsStyleBold));
        OnPropertyChanged(nameof(IsStyleItalic));
    }
}
