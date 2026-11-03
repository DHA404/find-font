using System.Windows;
using System.Windows.Controls;
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

    private void OnCompareClick(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<FontItem> source = ViewModel.SelectedFonts.Count > 0
            ? ViewModel.SelectedFonts.ToList()
            : ViewModel.FocusedFont is not null
                ? new[] { ViewModel.FocusedFont }
                : Array.Empty<FontItem>();

        if (source.Count == 0)
        {
            MessageBox.Show(this, "请先在左侧选择 1 到 3 个字体。", "多字体对比",
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
}
