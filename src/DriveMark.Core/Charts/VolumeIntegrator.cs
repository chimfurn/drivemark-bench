using DriveMark.Core.Parsing;

namespace DriveMark.Core.Charts;

/// <summary>
/// 累计传输量积分器。
/// fio 的 bw 日志每行给出的是"该采样窗口内的平均速率（KiB/s）"，并没有直接给出累计字节数，
/// 所以横轴"累计传输量"由速率对时间做梯形积分得到：
/// <code>
/// volume[0] = rate[0] * t[0]                        // 首个采样窗口
/// volume[i] = volume[i-1] + (rate[i-1]+rate[i])/2 * (t[i]-t[i-1])
/// </code>
/// 即使采样间隔不均匀（fio 只在有 IO 的窗口写日志）也能算对。
/// </summary>
public sealed class VolumeIntegrator
{
    private readonly double[] _timesMs;    // 各采样时间（ms）
    private readonly double[] _ratesMib;   // 各采样时刻的瞬时速率（MiB/s）
    private readonly double[] _cumMib;     // 到该时刻的累计量（MiB）

    private VolumeIntegrator(double[] timesMs, double[] ratesMib, double[] cumMib)
    {
        _timesMs = timesMs;
        _ratesMib = ratesMib;
        _cumMib = cumMib;
    }

    /// <summary>没有数据时的空积分器。</summary>
    public static VolumeIntegrator Empty { get; } = new([], [], []);

    public bool IsEmpty => _timesMs.Length == 0;

    /// <summary>总累计量（MiB）。</summary>
    public double TotalMib => _cumMib.Length == 0 ? 0 : _cumMib[^1];

    /// <summary>采样点数。</summary>
    public int Count => _timesMs.Length;

    /// <summary>用带宽日志（KiB/s）构建积分器。</summary>
    public static VolumeIntegrator FromBandwidth(IEnumerable<FioLogSample> samples) =>
        Build(samples, static s => s.Value / 1024d); // KiB/s → MiB/s

    /// <summary>用 IOPS 日志构建积分器：IOPS × 块大小 = 字节量。</summary>
    public static VolumeIntegrator FromIops(IEnumerable<FioLogSample> samples) =>
        Build(samples, static s => s.BlockSize > 0
            ? s.Value * s.BlockSize / (1024d * 1024d) // IOPS*bs → MiB/s
            : 0d);

    private static VolumeIntegrator Build(IEnumerable<FioLogSample> samples, Func<FioLogSample, double> rateSelector)
    {
        // 同一时间戳只保留最后一条，并按时间排序。
        var ordered = samples
            .Select(s => (s.TimeMs, Rate: rateSelector(s)))
            .Where(x => !double.IsNaN(x.Rate) && !double.IsInfinity(x.Rate) && x.Rate >= 0)
            .GroupBy(x => x.TimeMs)
            .Select(g => g.Last())
            .OrderBy(x => x.TimeMs)
            .ToList();

        if (ordered.Count == 0)
            return Empty;

        var times = new double[ordered.Count];
        var rates = new double[ordered.Count];
        var cum = new double[ordered.Count];

        for (var i = 0; i < ordered.Count; i++)
        {
            times[i] = ordered[i].TimeMs;
            rates[i] = ordered[i].Rate;
        }

        // 首个采样窗口 [0, t0] 按 r0 计。
        cum[0] = rates[0] * (times[0] / 1000d);

        for (var i = 1; i < ordered.Count; i++)
        {
            var dtSeconds = (times[i] - times[i - 1]) / 1000d;
            var avg = (rates[i - 1] + rates[i]) / 2d;
            cum[i] = cum[i - 1] + avg * dtSeconds;
        }

        return new VolumeIntegrator(times, rates, cum);
    }

    /// <summary>查询 t（毫秒）时刻的累计传输量（MiB）。</summary>
    public double CumulativeAt(double timeMs)
    {
        if (IsEmpty)
            return 0;
        if (timeMs <= 0)
            return 0;

        var last = _timesMs.Length - 1;

        if (timeMs <= _timesMs[0])
            return Math.Max(0, _ratesMib[0] * (timeMs / 1000d));

        if (timeMs >= _timesMs[last])
            return Math.Max(0, _cumMib[last] + _ratesMib[last] * ((timeMs - _timesMs[last]) / 1000d));

        // 二分查找所在区间 [lo, hi]。
        var lo = 0;
        var hi = last;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (_timesMs[mid] <= timeMs)
                lo = mid;
            else
                hi = mid;
        }

        var span = _timesMs[hi] - _timesMs[lo];
        var fraction = span <= 0 ? 0 : (timeMs - _timesMs[lo]) / span;
        var rateAt = _ratesMib[lo] + (_ratesMib[hi] - _ratesMib[lo]) * fraction;
        var dtSeconds = (timeMs - _timesMs[lo]) / 1000d;
        return Math.Max(0, _cumMib[lo] + rateAt * dtSeconds);
    }
}
