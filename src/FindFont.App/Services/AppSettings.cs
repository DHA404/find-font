using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace FindFont.App.Services;

/// <summary>
/// 用户设置：窗口位置/大小、上次的搜索与样本文字、字号、筛选状态。
///
/// 存在 %LOCALAPPDATA%\find-font\settings.json —— 不用 Properties.Settings，
/// 那套机制在单文件发布下有配置文件路径的坑，而且改一次字段就要动设计器。
///
/// 写入是延迟批量的：拖动窗口会让 Width/Height/Left/Top 每帧变一次，
/// 逐次写盘会把磁盘当乐器敲，所以只有 RequestSave 之后 500 ms 才真正落盘。
/// </summary>
public sealed class AppSettings
{
    private const int SaveDelayMs = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly Lazy<AppSettings> CurrentLazy = new(() => Load());

    private readonly object _gate = new();
    private Timer? _timer;
    private bool _dirty;

    /// <summary>公开无参构造：System.Text.Json 反序列化需要，测试也要能直接造实例。</summary>
    public AppSettings()
    {
    }

    /// <summary>进程内单例（首次访问时从磁盘读）。</summary>
    public static AppSettings Current => CurrentLazy.Value;

    // ---- 窗口 ----

    public double WindowWidth { get; set; } = 1600;
    public double WindowHeight { get; set; } = 1000;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; }

    // ---- 内容 ----

    public string PreviewText { get; set; } = "The quick brown fox 0123 你好，世界";
    public double PreviewFontSize { get; set; } = 28;
    public int SelectedCategoryIndex { get; set; }
    public int SelectedStyleIndex { get; set; }
    public string? LastSearchText { get; set; }
    public bool ShowBrokenOnly { get; set; }

    /// <summary>列表排序方式，对应 MainViewModel.SortOptions 的索引。</summary>
    public int SortIndex { get; set; }

    /// <summary>设置文件路径。</summary>
    public static string FilePath
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local))
            {
                local = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
                return Path.Combine(local, ".find-font", "settings.json");
            }

            return Path.Combine(local, "find-font", "settings.json");
        }
    }

    /// <summary>从磁盘读；文件缺失/损坏时返回默认值（绝不因为设置文件而启动失败）。</summary>
    public static AppSettings Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return new AppSettings();

            var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return new AppSettings();

            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return loaded ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Helpers.Diagnostics.Log(ex, "settings load failed");
            return new AppSettings();
        }
    }

    /// <summary>标记为已修改，500 ms 后落盘（重复调用会重置计时）。</summary>
    public void RequestSave()
    {
        lock (_gate)
        {
            _dirty = true;
            _timer ??= new Timer(_ => Flush(), null, SaveDelayMs, Timeout.Infinite);
            _timer.Change(SaveDelayMs, Timeout.Infinite);
        }
    }

    /// <summary>立即落盘（退出时调用）。</summary>
    public void Save()
    {
        lock (_gate)
        {
            _dirty = false;
        }

        Flush();
    }

    private void Flush()
    {
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
        }

        try
        {
            var path = FilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(this, JsonOptions);

            // 先写临时文件再替换：写到一半断电不会留下半截 JSON 让下次启动读崩。
            var temp = path + ".tmp";
            File.WriteAllText(temp, json, System.Text.Encoding.UTF8);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
        {
            Helpers.Diagnostics.Log(ex, "settings save failed");
        }
    }

    // ---- 窗口几何 ----

    /// <summary>把当前窗口几何记进设置。</summary>
    public void CaptureWindow(Window window)
    {
        if (window is null) return;

        WindowMaximized = window.WindowState == WindowState.Maximized;

        // 最大化时 RestoreBounds 才是用户想要的“还原后尺寸”。
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;

        if (bounds.IsEmpty || double.IsNaN(bounds.Width) || bounds.Width <= 0) return;

        WindowLeft = bounds.Left;
        WindowTop = bounds.Top;
        WindowWidth = bounds.Width;
        WindowHeight = bounds.Height;
    }

    /// <summary>
    /// 把设置里的几何套回窗口。窗口位置落在所有屏幕之外时（拔掉显示器、改过分辨率）
    /// 放弃坐标只保留尺寸，让 WindowStartupLocation 把它居中，避免窗口“打开后找不到”。
    /// </summary>
    public void ApplyWindow(Window window)
    {
        if (window is null) return;

        if (WindowWidth > 0) window.Width = Math.Max(window.MinWidth, WindowWidth);
        if (WindowHeight > 0) window.Height = Math.Max(window.MinHeight, WindowHeight);

        if (double.IsNaN(WindowLeft) || double.IsNaN(WindowTop)) return;
        if (!IsOnSomeScreen(WindowLeft, WindowTop)) return;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = WindowLeft;
        window.Top = WindowTop;

        if (WindowMaximized) window.WindowState = WindowState.Maximized;
    }

    /// <summary>坐标是否落在某块屏幕的可见范围内（含一点点容差）。</summary>
    public static bool IsOnSomeScreen(double left, double top)
    {
        const double margin = 80; // 至少露出这么多像素才算“能点到”

        try
        {
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
            var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

            return left + margin >= virtualLeft
                && top + margin >= virtualTop
                && left + margin <= virtualRight
                && top + margin <= virtualBottom;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
