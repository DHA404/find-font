using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindFont.App.Models;
using FindFont.App.Services;

namespace FindFont.App.ViewModels;

/// <summary>对比窗口视图模型：最多三栏字体同屏对照。</summary>
public partial class CompareViewModel : ObservableObject
{
    public const int MaxColumns = 3;

    public ObservableCollection<FontItem> Items { get; } = new();

    [ObservableProperty] private string _previewText = AppServices.PreviewText;
    [ObservableProperty] private double _previewFontSize = 64;

    public IReadOnlyList<double> FontSizeOptions { get; } = new[] { 40d, 52d, 64d, 80d, 104d, 128d };

    public CompareViewModel()
    {
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ColumnCount));
            OnPropertyChanged(nameof(IsEmpty));
            CompareTextCommand.NotifyCanExecuteChanged();
        };
    }

    public int ColumnCount => Items.Count;
    public bool IsEmpty => Items.Count == 0;

    partial void OnPreviewTextChanged(string value) => InvalidatePreviews();

    partial void OnPreviewFontSizeChanged(double value) => InvalidatePreviews();

    /// <summary>
    /// 文本或字号变了，旧位图必须作废，否则转换器会继续交回缓存图。
    /// 不再整池 Clear 缓存 —— 只清这两栏的大图槽位，其余字体的缓存照旧可用。
    /// </summary>
    private void InvalidatePreviews()
    {
        foreach (var item in Items)
        {
            item.PreviewBitmapLarge = null;
            item.IsPreviewPending = false;
            item.RequestLargePreview(PreviewText, PreviewFontSize,
                MainViewModel.LargePreviewWidth, MainViewModel.LargePreviewHeight);
        }
    }

    public void Load(IEnumerable<FontItem> fonts)
    {
        Items.Clear();
        foreach (var font in fonts.Take(MaxColumns)) Items.Add(font);
        if (Items.Count == 0 && !string.IsNullOrWhiteSpace(AppServices.PreviewText))
            PreviewText = AppServices.PreviewText;

        // 主界面已经不再为选中项渲染大图（改成瓦片小图），对比窗口必须自己请求，
        // 否则三栏会一直停在「正在渲染…」。
        foreach (var item in Items)
        {
            item.RequestLargePreview(PreviewText, PreviewFontSize,
                MainViewModel.LargePreviewWidth, MainViewModel.LargePreviewHeight);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCompareText))]
    private void CompareText()
    {
        // 文本没变，只是用户想强制重出图：重投一次大图渲染即可。
        InvalidatePreviews();
    }

    private bool CanCompareText() => Items.Count > 0;

    [RelayCommand]
    private void Remove(FontItem? item)
    {
        if (item is null) return;
        Items.Remove(item);
    }
}
