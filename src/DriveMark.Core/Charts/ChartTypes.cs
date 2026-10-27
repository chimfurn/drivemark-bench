namespace DriveMark.Core.Charts;

/// <summary>X 轴维度：时间 或 累计传输量（写入量）。</summary>
public enum XAxisDimension
{
    /// <summary>已运行时间，单位秒。</summary>
    ElapsedSeconds = 0,

    /// <summary>累计传输数据量，单位 MiB（写测试即"写入量"）。</summary>
    TransferredMebibytes = 1,
}

/// <summary>Y 轴指标：IOPS、吞吐量 或 延迟。</summary>
public enum YAxisMetric
{
    /// <summary>每秒 IO 次数。</summary>
    Iops = 0,

    /// <summary>吞吐量，单位 MiB/s。</summary>
    ThroughputMebibytesPerSecond = 1,

    /// <summary>平均延迟（来自 fio 的 lat 日志，原始单位纳秒，绘图时换算为 µs/ms）。</summary>
    Latency = 2,
}

/// <summary>曲线角色，决定配色（读=蓝，写=红）。</summary>
public enum SeriesRole
{
    Read,
    Write,
    Other,
}

/// <summary>图表呈现方式：折线图 或 散点图。</summary>
public enum ChartRenderStyle
{
    /// <summary>折线图（带渐变面积，适合看趋势）。</summary>
    Line = 0,

    /// <summary>散点图（只画数据点，适合看离散/抖动）。</summary>
    Scatter = 1,
}

/// <summary>一个数据点。</summary>
public readonly record struct ChartPoint(double X, double Y);

/// <summary>一条曲线。</summary>
public sealed record ChartSeries(
    string Name,
    SeriesRole Role,
    IReadOnlyList<ChartPoint> Points,
    bool Dashed = false)
{
    public int Count => Points.Count;

    public double MaxY => Points.Count == 0 ? 0 : Points.Max(p => p.Y);
    public double MinY => Points.Count == 0 ? 0 : Points.Min(p => p.Y);
    public double MaxX => Points.Count == 0 ? 0 : Points.Max(p => p.X);
    public double MinX => Points.Count == 0 ? 0 : Points.Min(p => p.X);

    /// <summary>
    /// 平均 Y：按 X 区间做梯形加权平均（时间轴≈时间加权平均，比"每个采样点等权"更接近 fio 官方平均值，
    /// 也不会被极短窗口的异常点大幅拉高）。X 区间全为 0 时退回等权平均。
    /// </summary>
    public double MeanY
    {
        get
        {
            if (Points.Count == 0)
                return 0;
            if (Points.Count == 1)
                return Points[0].Y;

            double area = 0, width = 0;
            for (var i = 1; i < Points.Count; i++)
            {
                var dx = Points[i].X - Points[i - 1].X;
                if (dx <= 0 || double.IsNaN(dx) || double.IsInfinity(dx))
                    continue;
                area += (Points[i - 1].Y + Points[i].Y) / 2d * dx;
                width += dx;
            }

            return width > 0 ? area / width : Points.Average(point => point.Y);
        }
    }
}

/// <summary>
/// 图表模型：轴规格（自定义坐标轴）+ 曲线 + 标题。
/// 渲染器只依赖了这个模型，与 WPF 无关；WPF 控件只是把它画出来。
/// </summary>
public sealed record ChartModel(
    string Title,
    string? Subtitle,
    string XTitle,
    string YTitle,
    AxisSpec XAxis,
    AxisSpec YAxis,
    IReadOnlyList<ChartSeries> Series,
    Func<double, string>? XFormatter = null,
    Func<double, string>? YFormatter = null,
    string? EmptyMessage = null,
    string? YUnit = null)
{
    /// <summary>呈现方式：折线或散点（默认为折线）。</summary>
    public ChartRenderStyle RenderStyle { get; init; } = ChartRenderStyle.Line;
    public bool HasData => Series.Any(s => s.Points.Count > 0);

    public AxisRange ResolveX() =>
        AxisResolver.Resolve(XAxis, Series.SelectMany(s => s.Points).Select(p => p.X));

    public AxisRange ResolveY() =>
        AxisResolver.Resolve(YAxis, Series.SelectMany(s => s.Points).Select(p => p.Y));

    public string FormatX(double value) => (XFormatter ?? Units.Plain)(value);
    public string FormatY(double value) => (YFormatter ?? Units.Plain)(value);

    /// <summary>X 轴标题。</summary>
    public static string XAxisTitle(XAxisDimension dimension) => dimension switch
    {
        XAxisDimension.ElapsedSeconds => AppText.T("时间 (s)", "Time (s)"),
        XAxisDimension.TransferredMebibytes => AppText.T("累计传输量 (MiB)", "Transferred (MiB)"),
        _ => "X",
    };

    /// <summary>Y 轴标题。</summary>
    public static string YAxisTitle(YAxisMetric metric) => metric switch
    {
        YAxisMetric.Iops => "IOPS",
        YAxisMetric.ThroughputMebibytesPerSecond => AppText.T("吞吐量 (MiB/s)", "Throughput (MiB/s)"),
        YAxisMetric.Latency => AppText.T("延迟 (µs)", "Latency (µs)"),
        _ => "Y",
    };

    /// <summary>Y 轴单位的简短写法，用于图例数值。</summary>
    public static string YAxisUnit(YAxisMetric metric) => metric switch
    {
        YAxisMetric.Iops => "IOPS",
        YAxisMetric.ThroughputMebibytesPerSecond => "MiB/s",
        YAxisMetric.Latency => "µs",
        _ => string.Empty,
    };

    public static Func<double, string> XFormatterFor(XAxisDimension dimension) => dimension switch
    {
        XAxisDimension.ElapsedSeconds => Units.SecondsAxis,
        _ => Units.Plain,
    };

    public static Func<double, string> YFormatterFor(YAxisMetric metric) => metric switch
    {
        YAxisMetric.Iops => Units.IopsAxis,
        YAxisMetric.Latency => Units.Plain,
        _ => Units.ThroughputAxis,
    };

    public static readonly ChartModel Empty = new(
        AppText.T("fio 曲线", "fio charts"), null, AppText.T("时间 (s)", "Time (s)"), "IOPS", AxisSpec.Auto, AxisSpec.Auto, [],
        XFormatter: Units.SecondsAxis, YFormatter: Units.IopsAxis,
        EmptyMessage: AppText.T("没有数据", "No data"), YUnit: "IOPS");
}
