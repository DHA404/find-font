using CommunityToolkit.Mvvm.ComponentModel;
using FindFont.App.Models;
using FindFont.App.Services;

namespace FindFont.App.ViewModels;

/// <summary>
/// 字形覆盖检测的视图模型部分：什么时候量、量完怎么写回、以及两个筛选开关。
/// <para>
/// 纪律：检测永远排在扫描与补名之后异步进行，绝不 await 进首屏；
/// 写回 FontItem 一律回 UI 线程，线程池线程只负责算。
/// </para>
/// </summary>
public partial class MainViewModel
{
    private const int CoverageDebounceMs = 300;

    private CancellationTokenSource? _coverageCts;

    /// <summary>上一次真正量过的探测指纹，避免样品文字没变就重跑。</summary>
    private int _measuredProbeHash = -1;

    /// <summary>只留下「码位没有映射」的字体。</summary>
    [ObservableProperty] private bool _showMissingMapOnly;

    /// <summary>只留下「有映射但轮廓为空」的字体。</summary>
    [ObservableProperty] private bool _showEmptyGlyphOnly;

    /// <summary>覆盖检测进行中（状态栏用）。</summary>
    [ObservableProperty] private bool _isCoverageBusy;

    /// <summary>当前样品串下真正有缺字的字体数。</summary>
    [ObservableProperty] private int _coverageHoleCount;

    partial void OnShowMissingMapOnlyChanged(bool value) => ApplyFilter();

    partial void OnShowEmptyGlyphOnlyChanged(bool value) => ApplyFilter();

    /// <summary>当前用来检测的样品串。</summary>
    private string CoverageProbeText =>
        string.IsNullOrWhiteSpace(PreviewText) ? AppServices.PreviewText : PreviewText;

    /// <summary>
    /// 量一遍所有字体的字形覆盖。已有同样探测集的缓存时不重复读盘，只把结果写回。
    /// </summary>
    public async Task RefreshCoverageAsync()
    {
        // 扫描还没跑完就量没有意义，也会把状态栏的「正在读取注册表」盖掉。
        if (_allFonts.Count == 0 || IsLoading) return;

        var text = CoverageProbeText;
        var codepoints = FontCoverageIndex.ToCodepoints(text);
        if (codepoints.Count == 0) return;

        var probeHash = FontCoverageIndex.ProbeHash(codepoints);
        var snapshot = _allFonts.ToList();

        // 探测集没变且已经量过：只把缓存写回缺失的项，避免重复读盘。
        if (probeHash == _measuredProbeHash && AllMeasured(snapshot)) return;

        _coverageCts?.Cancel();
        _coverageCts?.Dispose();
        _coverageCts = new CancellationTokenSource();
        var token = _coverageCts.Token;

        IsCoverageBusy = true;
        StatusText = "正在检测字形覆盖…";

        int measured;
        try
        {
            measured = await Task.Run(
                () => AppServices.CoverageIndex.Measure(snapshot, codepoints, token),
                token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            IsCoverageBusy = false;
            return;
        }
        catch (Exception ex) when (Helpers.FontMetadataReader.IsRecoverable(ex))
        {
            IsCoverageBusy = false;
            StatusText = "字形覆盖检测失败：" + ex.Message;
            Helpers.Diagnostics.Log(ex, "coverage");
            return;
        }

        if (token.IsCancellationRequested) return;

        ApplyCoverageFromCache(snapshot);
        _measuredProbeHash = probeHash;
        IsCoverageBusy = false;

        var holes = snapshot.Count(item => item.HasCoverageHoles);
        CoverageHoleCount = holes;

        var basis = measured == 0 ? "命中缓存" : $"新量 {measured} 个";
        StatusText = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"字形覆盖检测完成：{holes} 个字体在「{text}」下缺字（{basis}）");
    }

    /// <summary>样品文字改动后防抖重测，避免每敲一个字符就量一遍全部字体。</summary>
    private void DebounceCoverage()
    {
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _coverageDebounceCts, cts);
        previous?.Cancel();
        previous?.Dispose();

        var token = cts.Token;
        var ui = SynchronizationContext.Current;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(CoverageDebounceMs, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested) return;
                    if (ui is null) _ = RefreshCoverageAsync();
                    else ui.Post(_ => _ = RefreshCoverageAsync(), null);
                }
                catch (OperationCanceledException)
                {
                    // 用户还在打字，这一轮直接作废。
                }
            },
            token);
    }

    private CancellationTokenSource? _coverageDebounceCts;

    /// <summary>把缓存里已有的结果写回每个字体（UI 线程调用）。</summary>
    internal void ApplyCoverageFromCache(IEnumerable<FontItem> items)
    {
        foreach (var item in items)
        {
            if (item.IsBroken)
            {
                item.CoveragePending = false;
                continue;
            }

            item.Coverage = AppServices.CoverageIndex.TryGetCached(item);
            item.CoveragePending = false;
        }
    }

    private bool AllMeasured(IEnumerable<FontItem> items)
    {
        foreach (var item in items)
        {
            if (item.IsBroken) continue;
            if (AppServices.CoverageIndex.TryGetCached(item) is null) return false;
        }

        return true;
    }

    /// <summary>切到别的字体集合（筛选/排序后）时，把缓存结果补回新出现的行。</summary>
    private void SyncCoverageToVisible()
    {
        if (_measuredProbeHash < 0) return;
        foreach (var item in VisibleFonts)
        {
            if (item.Coverage is not null || item.IsBroken) continue;
            var cached = AppServices.CoverageIndex.TryGetCached(item);
            if (cached is not null) item.Coverage = cached;
        }
    }
}
