using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FindFont.App.Helpers;
using FindFont.App.Models;
using FindFont.App.Services;
using FindFont.App.ViewModels;

namespace FindFont.App.Views;

public partial class MainWindow : Window
{
    /// <summary>单行入场位移的起点：只有一点点，够让人感知到「换了一批」，不做过场特效。</summary>
    private const double EntranceOffset = 14;

    /// <summary>相邻两行入场的时间差，超过上限后不再累加，避免长列表尾部等太久。</summary>
    private const double StaggerStepMs = 12;

    private const double StaggerCapMs = 180;

    private bool _entrancePending;

    private bool _firstLoadDone;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        ViewModel.FilterApplied += OnFilterApplied;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        DpiChanged += OnDpiChanged;
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        // 先把 DPI 报给渲染层：高分屏上按 96 出图再被系统拉伸会发虚。
        RenderDpi.Track(this);

        // 窗口入场：整块内容淡入，纸色底已经在，不会闪白。
        if (Motion.Enabled)
        {
            PaperLayer.Opacity = 0;
            PaperLayer.BeginAnimation(OpacityProperty, Motion.Hold(1, Motion.Slow));
        }

        ViewModel.RestoreSettings(AppSettings.Current);
        await ViewModel.LoadAsync();
        _firstLoadDone = true;
    }

    /// <summary>跨屏拖动改了缩放：更新 DPI 并让所有位图按新 DPI 重出。</summary>
    private void OnDpiChanged(object sender, DpiChangedEventArgs e)
    {
        RenderDpi.Track(this);
        ViewModel.RefreshDpi();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        ViewModel.PersistSettings(AppSettings.Current);
        AppSettings.Current.CaptureWindow(this);
        AppSettings.Current.Save();
        base.OnClosing(e);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 补名阶段会反复 ApplyFilter，只有真正稳定下来那次才播入场，否则列表一直在抖。
        if (e.PropertyName == nameof(MainViewModel.IsLoading) && !ViewModel.IsLoading && _firstLoadDone)
        {
            QueueEntrance();
        }
    }

    private void OnFilterApplied(object? sender, EventArgs e) => QueueEntrance();

    /// <summary>
    /// 筛选入场：等容器真正生成后再播，且只播一次。
    /// 用 Tag 标记「这一行已经入过场」，回收出来的容器换数据后 Tag 会跟着清掉，能重新播。
    /// </summary>
    private void QueueEntrance()
    {
        if (!Motion.Enabled || _entrancePending) return;

        _entrancePending = true;
        Dispatcher.BeginInvoke(new Action(RunEntrance), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void RunEntrance()
    {
        _entrancePending = false;

        // 只遍历已实现的容器：全表 1333 次 ContainerFromIndex 会在虚拟化下白跑一遍，
        // 连续若干次取不到就说明已经走到未实现区域，直接停下。
        var misses = 0;
        for (var i = 0; i < FontList.Items.Count; i++)
        {
            if (FontList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container)
            {
                if (++misses > 4) break;
                continue;
            }

            misses = 0;
            if (container.Tag is true) continue;

            container.Tag = true;
            AnimateContainer(container, i);
        }
    }

    private static void AnimateContainer(ListBoxItem container, int index)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Min(index * StaggerStepMs, StaggerCapMs));

        if (container.RenderTransform is not TranslateTransform move)
        {
            move = new TranslateTransform();
            container.RenderTransform = move;
        }

        container.Opacity = 0;
        move.X = EntranceOffset;

        var fade = Motion.Hold(1, Motion.Base);
        fade.BeginTime = delay;
        container.BeginAnimation(OpacityProperty, fade);

        var slide = Motion.To(0, Motion.Base);
        slide.BeginTime = delay;
        move.BeginAnimation(TranslateTransform.XProperty, slide);
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

    /// <summary>
    /// 拖放安装：只有在拖的是文件、且至少有一个受支持格式时才显示「可放下」光标，
    /// 否则保持禁止样式，用户不必拖进去才知道不行。
    /// </summary>
    private void OnFilesDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetSupportedFiles(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFilesDropped(object sender, DragEventArgs e)
    {
        var all = GetAllDroppedFiles(e);
        if (all.Count == 0)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;

        // 整批交给安装逻辑：它会把不支持的格式逐个说明原因，
        // 在这里先筛掉的话用户就永远不知道自己拖进来的那半个文件为什么没反应。
        await ViewModel.InstallFilesAsync(all);
    }

    private static List<string> GetAllDroppedFiles(DragEventArgs e)
        => e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths
            ? new List<string>(paths)
            : new List<string>();

    /// <summary>拖拽悬停时只关心「有没有至少一个能装的文件」，用来决定光标样式。</summary>
    private static List<string> GetSupportedFiles(DragEventArgs e)
        => GetAllDroppedFiles(e).Where(FontInstaller.IsSupportedFontFile).ToList();
}
