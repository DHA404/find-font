using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace FindFont.App.Helpers;

/// <summary>
/// 启动耗时打点。
/// <para>
/// 从进程静态构造那一刻起计时，各处只调用 <see cref="Mark"/> 记一个相对毫秒数。
/// 目的是让「启动慢」这种模糊抱怨变成具体数字 —— 用户报「要等两秒」，我们能立刻
/// 分清是引导器、运行时检测、注册表扫描，还是首屏出图。
/// </para>
/// <para>
/// 只保留最近一次启动的打点，不落盘、不上报；「关于」窗口里展示，也供测试断言顺序。
/// </para>
/// </summary>
public static class StartupTrace
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly List<(string Label, double Ms)> Marks = new(16);
    private static readonly object Sync = new();

    /// <summary>记一个打点。标签重复时保留最后一次，避免重扫时越积越多。</summary>
    public static void Mark(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return;

        var elapsed = Clock.Elapsed.TotalMilliseconds;
        lock (Sync)
        {
            for (var i = 0; i < Marks.Count; i++)
            {
                if (Marks[i].Label != label) continue;

                Marks[i] = (label, elapsed);
                return;
            }

            Marks.Add((label, elapsed));
        }
    }

    /// <summary>当前距进程启动的毫秒数，供日志与测试使用。</summary>
    public static double ElapsedMs => Clock.Elapsed.TotalMilliseconds;

    /// <summary>某次启动至今的打点快照（标签 → 相对毫秒）。</summary>
    public static IReadOnlyList<KeyValuePair<string, double>> Snapshot()
    {
        lock (Sync)
        {
            return Marks.Select(m => new KeyValuePair<string, double>(m.Label, m.Ms)).ToList();
        }
    }

    /// <summary>渲染成多行文本，供「关于」窗口与日志使用。</summary>
    public static string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine("启动耗时（相对进程启动，毫秒）");

        var marks = Snapshot();
        if (marks.Count == 0) return sb.Append("（无打点）").ToString();

        double previous = 0;
        foreach (var mark in marks)
        {
            var delta = mark.Value - previous;
            sb.Append("  ")
              .Append(mark.Key.PadRight(22))
              .Append(mark.Value.ToString("F0", CultureInfo.InvariantCulture).PadLeft(6))
              .Append("  (+")
              .Append(delta.ToString("F0", CultureInfo.InvariantCulture))
              .AppendLine(")");
            previous = mark.Value;
        }

        return sb.ToString().TrimEnd();
    }
}
