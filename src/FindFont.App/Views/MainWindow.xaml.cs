using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FindFont.App.Models;
using FindFont.App.ViewModels;

namespace FindFont.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await ViewModel.LoadAsync();
    }

    private void OnFontSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox listBox) return;

        var selected = new List<FontItem>();
        foreach (var item in listBox.SelectedItems)
        {
            if (item is FontItem font) selected.Add(font);
        }

        ViewModel.UpdateSelection(selected);
    }

    /// <summary>双击样本行直接放大对比（单个字体也允许）。</summary>
    private void OnSpecimenDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.SelectedFonts.Count == 0) return;
        OpenCompareWindow();
    }

    private void OnCompareClick(object sender, RoutedEventArgs e) => OpenCompareWindow();

    private void OpenCompareWindow()
    {
        IReadOnlyList<FontItem> source = ViewModel.SelectedFonts.Count > 0
            ? ViewModel.SelectedFonts.ToList()
            : ViewModel.FocusedFont is not null
                ? new[] { ViewModel.FocusedFont }
                : Array.Empty<FontItem>();

        if (source.Count == 0)
        {
            MessageBox.Show(this, "先在左侧选 1 到 3 个字体。", "对比选中",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (source.Count > CompareViewModel.MaxColumns)
        {
            source = source.Take(CompareViewModel.MaxColumns).ToList();
        }

        var window = new CompareWindow { Owner = this };
        if (window.DataContext is CompareViewModel vm)
        {
            vm.PreviewText = ViewModel.PreviewText;
            vm.Load(source);
        }

        window.ShowDialog();
    }

    /// <summary>Ctrl+F 聚焦搜索，Esc 先清搜索再清选择，回车直接对比。</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (!string.IsNullOrWhiteSpace(ViewModel.SearchText))
            {
                ViewModel.ClearSearchCommand.Execute(null);
                SearchBox.Focus();
            }
            else if (FontList.SelectedItems.Count > 0)
            {
                FontList.SelectedItems.Clear();
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && ViewModel.SelectedFonts.Count > 0)
        {
            OpenCompareWindow();
            e.Handled = true;
        }
    }
}
