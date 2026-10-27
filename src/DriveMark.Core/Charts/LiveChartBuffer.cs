using DriveMark.Core.Parsing;

namespace DriveMark.Core.Charts;

/// <summary>
/// 实时绘图缓冲区：把 fio 的进度行逐条累积成曲线数据。
///
/// 与"测试完成后读日志出图"不同，实时模式**只在内存里更新图表**，不会生成任何 PNG 文件。
/// 启动/布局阶段没有速率的进度行会被忽略，并且横轴从**第一个有效数据点**开始计时，
/// 因此等待启动的那几秒不会进入图像。
/// </summary>
public sealed class LiveChartBuffer
{
    private readonly record struct RawSample(
        double ElapsedSeconds,
        double? ReadMibps,
        double? ReadIops,
        double? WriteMibps,
        double? WriteIops);

    private readonly List<RawSample> _samples = [];
    private double? _originSeconds;

    /// <summary>已累积的有效采样数。</summary>
    public int Count => _samples.Count;

    public bool HasData => _samples.Count > 0;

    /// <summary>实时模式不支持延迟（进度行不含延迟数据）。</summary>
    public static bool SupportsMetric(YAxisMetric metric) => metric != YAxisMetric.Latency;

    public void Clear()
    {
        _samples.Clear();
        _originSeconds = null;
    }

    /// <summary>
    /// 加入一条进度采样。没有速率/IOPS 的进度行（例如启动、布局测试文件阶段）返回 false 且不记录。
    /// </summary>
    public bool Add(TimeSpan elapsed, ProgressSample sample)
    {
        var hasRead = sample.ReadMebibytesPerSecond is not null || sample.ReadIops is not null;
        var hasWrite = sample.WriteMebibytesPerSecond is not null || sample.WriteIops is not null;
        if (!hasRead && !hasWrite)
            return false;

        _originSeconds ??= elapsed.TotalSeconds;
        _samples.Add(new RawSample(
            elapsed.TotalSeconds,
            sample.ReadMebibytesPerSecond,
            sample.ReadIops,
            sample.WriteMebibytesPerSecond,
            sample.WriteIops));
        return true;
    }

    /// <summary>当前数据在给定横轴维度下的最大 X 值（用于滚动条/跟随最新）。</summary>
    public double ComputeSpan(XAxisDimension dimension)
    {
        if (!HasData)
            return 0;

        if (dimension == XAxisDimension.ElapsedSeconds)
            return Math.Max(0, _samples[^1].ElapsedSeconds - (_originSeconds ?? 0));

        // 传输量：取读写两个方向累计量的较大者。
        var read = ComputeVolume(0);
        var write = ComputeVolume(1);
        return Math.Max(read, write);
    }

    /// <summary>
    /// 构建实时图表模型。
    /// </summary>
    /// <param name="xDimension">横轴。</param>
    /// <param name="yMetric">纵轴（延迟不支持，会返回带提示的空图）。</param>
    /// <param name="style">折线/散点。</param>
    /// <param name="windowSeconds">只显示最近 N 秒/单位（null=显示全部）。</param>
    /// <param name="windowEndSeconds">窗口右端（默认最新数据处）。</param>
    public ChartModel Build(
        XAxisDimension xDimension,
        YAxisMetric yMetric,
        ChartRenderStyle style,
        double? windowSeconds = null,
        double? windowEndSeconds = null,
        string? title = null,
        string? subtitle = null)
    {
        if (yMetric == YAxisMetric.Latency)
        {
            return new ChartModel(
                title ?? AppText.T("实时绘图", "Realtime chart"),
                subtitle ?? AppText.T("实时绘图不支持延迟", "Realtime chart does not support latency"),
                ChartModel.XAxisTitle(xDimension),
                ChartModel.YAxisTitle(yMetric),
                AxisSpec.AutoFromZero(),
                AxisSpec.AutoFromZero(),
                [],
                XFormatter: ChartModel.XFormatterFor(xDimension),
                YFormatter: Units.Plain,
                EmptyMessage: AppText.T("实时绘图不支持延迟曲线：请把测试跑完后再切换「完成后绘图」查看延迟。", "Realtime chart does not support latency; run the test to completion, then switch to 'After test' to view latency."))
            {
                RenderStyle = style,
            };
        }

        var series = new List<ChartSeries>();
        foreach (var direction in new[] { 0, 1 })
        {
            var points = BuildPoints(direction, xDimension, yMetric);
            if (points.Count == 0)
                continue;

            series.Add(new ChartSeries(
                direction == 0 ? AppText.T("读 (read)", "Read") : AppText.T("写 (write)", "Write"),
                direction == 0 ? SeriesRole.Read : SeriesRole.Write,
                points));
        }

        var span = series.Count == 0
            ? 0
            : series.Max(s => s.Points.Count == 0 ? 0 : s.Points.Max(p => p.X));

        // 纵轴刻度与完成绘图保持一致：吞吐量用具体数值，IOPS 按轴最大值决定是否用 k 缩写。
        var axisMaxY = series.Count == 0
            ? 0
            : series.Max(s => s.Points.Count == 0 ? 0 : s.Points.Max(p => p.Y));
        var yFormatter = yMetric switch
        {
            YAxisMetric.Iops => Units.IopsAxisFor(axisMaxY),
            _ => ChartModel.YFormatterFor(yMetric),
        };
        var xFormatter = xDimension == XAxisDimension.TransferredMebibytes
            ? Units.PlainConcrete
            : ChartModel.XFormatterFor(xDimension);

        AxisSpec xAxis = AxisSpec.AutoFromZero();
        if (windowSeconds is > 0)
        {
            var end = Math.Max(windowEndSeconds ?? span, windowSeconds.Value);
            var start = Math.Max(0, Math.Min(end - windowSeconds.Value, span));
            xAxis = AxisSpec.Manual(start, start + windowSeconds.Value);
        }

        var emptyMessage = series.Count == 0 && yMetric != YAxisMetric.Latency
            ? AppText.T("等待测试数据…（开始测试后曲线会实时更新）", "Waiting for test data… (the chart updates once the test starts)")
            : null;

        return new ChartModel(
            title ?? AppText.T("实时绘图", "Realtime chart"),
            subtitle ?? AppText.T("实时绘图 · 数据边测边画（不生成图片文件）", "Realtime chart · samples the test as it runs (no image files)"),
            ChartModel.XAxisTitle(xDimension),
            ChartModel.YAxisTitle(yMetric),
            xAxis,
            AxisSpec.AutoFromZero(),
            series,
            XFormatter: xFormatter,
            YFormatter: yFormatter,
            EmptyMessage: emptyMessage,
            YUnit: ChartModel.YAxisUnit(yMetric))
        {
            RenderStyle = style,
        };
    }

    private List<ChartPoint> BuildPoints(int direction, XAxisDimension xDimension, YAxisMetric yMetric)
    {
        var points = new List<ChartPoint>();
        double cumulative = 0;
        double? lastRate = null;
        double? lastTime = null;

        foreach (var sample in _samples)
        {
            var rate = direction == 0 ? sample.ReadMibps : sample.WriteMibps;
            var iops = direction == 0 ? sample.ReadIops : sample.WriteIops;
            var value = yMetric == YAxisMetric.Iops ? iops : rate;
            if (value is null)
                continue;

            var t = sample.ElapsedSeconds - (_originSeconds ?? sample.ElapsedSeconds);
            if (xDimension == XAxisDimension.TransferredMebibytes)
            {
                if (lastRate is not null && lastTime is not null)
                    cumulative += (lastRate.Value + (rate ?? lastRate.Value)) / 2d * Math.Max(0, t - lastTime.Value);
            }

            lastRate = rate ?? lastRate;
            lastTime = t;

            var x = xDimension == XAxisDimension.ElapsedSeconds ? t : cumulative;
            points.Add(new ChartPoint(Math.Max(0, x), value.Value));
        }

        return points;
    }

    private double ComputeVolume(int direction)
    {
        double cumulative = 0;
        double? lastRate = null;
        double? lastTime = null;

        foreach (var sample in _samples)
        {
            var rate = direction == 0 ? sample.ReadMibps : sample.WriteMibps;
            if (rate is null)
                continue;

            var t = sample.ElapsedSeconds - (_originSeconds ?? sample.ElapsedSeconds);
            if (lastRate is not null && lastTime is not null)
                cumulative += (lastRate.Value + rate.Value) / 2d * Math.Max(0, t - lastTime.Value);

            lastRate = rate;
            lastTime = t;
        }

        return cumulative;
    }
}
