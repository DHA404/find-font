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

    /// <summary>文本或字号变了，旧位图必须作废，否则转换器会继续交回缓存图。</summary>
    private void InvalidatePreviews()
    {
        AppServices.RenderCache.Clear();
        foreach (var item in Items)
        {
            item.PreviewBitmap = null;
            item.PreviewBitmapLarge = null;
            item.IsPreviewPending = false;
        }
    }

    public void Load(IEnumerable<FontItem> fonts)
    {
        Items.Clear();
        foreach (var font in fonts.Take(MaxColumns)) Items.Add(font);
        if (Items.Count == 0 && !string.IsNullOrWhiteSpace(AppServices.PreviewText))
            PreviewText = AppServices.PreviewText;
    }

    [RelayCommand(CanExecute = nameof(CanCompareText))]
    private void CompareText()
    {
        // 对比模式下文本变化会自动重渲染（绑定到 FontToImageConverter）；
        // 这里只需要让位图缓存失效，保证新文本立即出图。
        AppServices.RenderCache.Clear();
        foreach (var item in Items)
        {
            item.PreviewBitmap = null;
            item.PreviewBitmapLarge = null;
            item.IsPreviewPending = false;
        }
    }

    private bool CanCompareText() => Items.Count > 0;

    [RelayCommand]
    private void Remove(FontItem? item)
    {
        if (item is null) return;
        Items.Remove(item);
    }
}
