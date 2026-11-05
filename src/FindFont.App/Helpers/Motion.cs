using System.Windows;
using System.Windows.Media.Animation;

namespace FindFont.App.Helpers;

/// <summary>
/// 全应用唯一的运动语言：三个时长 + 一条缓动曲线。
/// 所有动画都从这里取参数，避免每个控件各自发明一套节奏。
/// 系统关闭「窗口和按钮动画」时整体降级为瞬时切换。
/// </summary>
public static class Motion
{
    /// <summary>窗口入场：只做半拍收束，不能让用户等动画。</summary>
    public static readonly Duration Instant = new(TimeSpan.FromMilliseconds(80));

    /// <summary>极短反馈：hover、press。</summary>
    public static readonly Duration Fast = new(TimeSpan.FromMilliseconds(120));

    /// <summary>常规状态切换：行展开、入场。</summary>
    public static readonly Duration Base = new(TimeSpan.FromMilliseconds(190));

    /// <summary>大幅布局变化：档案栏开合、窗口入场。</summary>
    public static readonly Duration Slow = new(TimeSpan.FromMilliseconds(260));

    /// <summary>统一的缓动：慢起快停，接近系统 Fluent 的手感。</summary>
    public static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    private static readonly Lazy<bool> CachedEnabled = new(() => Evaluate(SystemParameters.ClientAreaAnimation));

    /// <summary>当前是否应当播放动画。</summary>
    public static bool Enabled => CachedEnabled.Value;

    /// <summary>供测试直接断言的可注入判定，不依赖本机系统设置。</summary>
    public static bool Evaluate(bool animationsAllowed) => animationsAllowed;

    // XAML 用 x:Static 取这三个：系统关掉动画时它们退化成零时长，模板里不用写分支。
    public static Duration FastXaml => Enabled ? Fast : TimeSpan.Zero;

    public static Duration BaseXaml => Enabled ? Base : TimeSpan.Zero;

    public static Duration SlowXaml => Enabled ? Slow : TimeSpan.Zero;

    public static Duration InstantXaml => Enabled ? Instant : TimeSpan.Zero;

    /// <summary>按条件取「动画时长」或「零时长」，让代码里只有一条分支。</summary>
    public static Duration If(bool play) => play && Enabled ? Base : TimeSpan.Zero;

    public static DoubleAnimation To(double value, Duration duration) =>
        new(value, duration) { EasingFunction = Ease, FillBehavior = FillBehavior.Stop };

    /// <summary>
    /// 停在终值的动画。淡入这类「起点 0、终点 1」的场合必须用它：
    /// FillBehavior.Stop 会在播完后把属性还原成本地值 0，看起来就是元素直接消失。
    /// </summary>
    public static DoubleAnimation Hold(double value, Duration duration) =>
        new(value, duration) { EasingFunction = Ease, FillBehavior = FillBehavior.HoldEnd };

    public static DoubleAnimation FromTo(double from, double to, Duration duration) =>
        new(from, to, duration) { EasingFunction = Ease, FillBehavior = FillBehavior.Stop };
}
