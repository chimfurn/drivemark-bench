using DriveMark.Core.Parsing;

namespace DriveMark.Core.Charts;

/// <summary>
/// 把 <see cref="ChartDataSet"/>（fio 日志）按任意"横轴维度 × 纵轴指标"组合装配成
/// <see cref="ChartModel"/>。
///
/// 这是整个图表模块的中枢：界面上的四个选项（时间/传输量 × IOPS/吞吐量）
/// 只是这里的两个枚举参数，渲染代码完全不用改。
/// </summary>
public static class SeriesBuilder
{
    /// <summary>构建图表模型。</summary>
    /// <param name="data">日志数据集。</param>
    /// <param name="xDimension">横轴：时间 或 累计传输量。</param>
    /// <param name="yMetric">纵轴：IOPS 或 吞吐量。</param>
    /// <param name="xAxis">横轴自定义配置（null = 自动）。</param>
    /// <param name="yAxis">纵轴自定义配置（null = 自动）。</param>
    /// <param name="title">标题（null = 用数据集名）。</param>
    /// <param name="subtitle">副标题（null = 用数据集描述）。</param>
    public static ChartModel Build(
        ChartDataSet data,
        XAxisDimension xDimension,
        YAxisMetric yMetric,
        AxisSpec? xAxis = null,
        AxisSpec? yAxis = null,
        string? title = null,
        string? subtitle = null,
        ChartRenderStyle renderStyle = ChartRenderStyle.Line,
        bool zeroBasedTimeAxis = false)
    {
        var primary = yMetric switch
        {
            YAxisMetric.Iops => data.IopsSamples,
            YAxisMetric.Latency => data.LatencySamples,
            _ => data.BandwidthSamples,
        };

        var series = new List<ChartSeries>();

        foreach (var direction in FioLogParser.DistinctDirections(primary))
        {
            var directionSamples = FioLogParser.FilterDirection(primary, direction);
            if (directionSamples.Count == 0)
                continue;

            var integrator = BuildIntegrator(data, direction);
            var points = new List<ChartPoint>(directionSamples.Count);

            foreach (var sample in directionSamples)
            {
                var x = xDimension == XAxisDimension.ElapsedSeconds
                    ? sample.TimeMs / 1000d
                    : integrator.CumulativeAt(sample.TimeMs);

                var y = yMetric switch
                {
                    YAxisMetric.Iops => sample.Value,
                    YAxisMetric.Latency => sample.Value / 1000d, // 纳秒 → 微秒
                    _ => sample.Value / 1024d,                   // KiB/s → MiB/s
                };

                if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y))
                    continue;

                points.Add(new ChartPoint(x, y));
            }

            points = Normalize(points);
            if (points.Count == 0)
                continue;

            series.Add(new ChartSeries(SeriesName(direction, yMetric), RoleOf(direction), points));
        }

        // 延迟单位自适应：超过 2000µs 时改用 ms，避免轴上出现 "1.5k" 这种延迟读数。
        var yTitle = ChartModel.YAxisTitle(yMetric);
        var yFormatter = ChartModel.YFormatterFor(yMetric);
        var yUnit = ChartModel.YAxisUnit(yMetric);
        if (yMetric == YAxisMetric.Latency && series.Count > 0)
        {
            var maxY = series.Max(s => s.Points.Count == 0 ? 0 : s.Points.Max(p => p.Y));
            if (maxY >= 2000)
            {
                series = series
                    .Select(s => s with
                    {
                        Points = s.Points.Select(p => new ChartPoint(p.X, p.Y / 1000d)).ToList(),
                    })
                    .ToList();
                yTitle = AppText.T("延迟 (ms)", "Latency (ms)");
                yUnit = "ms";
            }
        }

        // 纵轴刻度格式：
        // - 吞吐量：始终具体数值（1000 而不是 1k）；
        // - IOPS：轴最大值 ≤ 10000 用具体整数，超过才用 k（>1M 用 M）；
        // - 延迟：用具体数值（µs/ms）。
        var axisMaxY = series.Count == 0
            ? 0
            : series.Max(s => s.Points.Count == 0 ? 0 : s.Points.Max(p => p.Y));
        yFormatter = yMetric switch
        {
            YAxisMetric.Iops => Units.IopsAxisFor(axisMaxY),
            YAxisMetric.Latency => Units.PlainConcrete,
            _ => Units.ThroughputAxis,
        };

        // 时间轴可选"从第一个数据点起算"：fio 启动/布局文件的等待时间不会进入图像。
        if (zeroBasedTimeAxis && xDimension == XAxisDimension.ElapsedSeconds && series.Count > 0)
        {
            var minX = series.Where(s => s.Points.Count > 0).Min(s => s.Points.Min(p => p.X));
            if (minX > 0)
            {
                series = series
                    .Select(s => s with
                    {
                        Points = s.Points.Select(p => new ChartPoint(p.X - minX, p.Y)).ToList(),
                    })
                    .ToList();
            }
        }

        var emptyMessage = BuildEmptyMessage(data, xDimension, yMetric, series.Count > 0);

        // 横轴单位自适应：传输量很大时从 MiB 升到 GiB，避免出现 "21k" 这种不直观的刻度。
        // 只有在自动缩放时才换算——用户手动指定范围时应保持其指定的 MiB 单位。
        var xTitle = ChartModel.XAxisTitle(xDimension);
        var xFormatter = ChartModel.XFormatterFor(xDimension);
        var autoX = xAxis is null || xAxis.AutoScale;
        if (xDimension == XAxisDimension.TransferredMebibytes)
        {
            // 传输量横轴同样使用具体数值（MiB 单位下 1500 显示 1500，而不是 1.5k）。
            xFormatter = Units.PlainConcrete;
        }

        if (autoX && xDimension == XAxisDimension.TransferredMebibytes && series.Count > 0)
        {
            var maxX = series.Max(s => s.Points.Count == 0 ? 0 : s.Points.Max(p => p.X));
            if (maxX >= 2048)
            {
                series = series
                    .Select(s => s with
                    {
                        Points = s.Points.Select(p => new ChartPoint(p.X / 1024d, p.Y)).ToList(),
                    })
                    .ToList();
                xTitle = AppText.T("累计传输量 (GiB)", "Transferred (GiB)");
                xFormatter = Units.PlainConcrete;
            }
        }

        return new ChartModel(
            Title: title ?? data.Name,
            Subtitle: subtitle ?? data.Description,
            XTitle: xTitle,
            YTitle: yTitle,
            XAxis: xAxis ?? DefaultXAxis(xDimension),
            YAxis: yAxis ?? DefaultYAxis(),
            Series: series,
            XFormatter: xFormatter,
            YFormatter: yFormatter,
            EmptyMessage: emptyMessage,
            YUnit: yUnit)
        {
            RenderStyle = renderStyle,
        };
    }

    /// <summary>默认横轴：从 0 起、自动缩放。</summary>
    public static AxisSpec DefaultXAxis(XAxisDimension dimension) =>
        dimension == XAxisDimension.ElapsedSeconds ? AxisSpec.AutoFromZero() : AxisSpec.AutoFromZero();

    /// <summary>默认纵轴：从 0 起、自动缩放。</summary>
    public static AxisSpec DefaultYAxis() => AxisSpec.AutoFromZero();

    /// <summary>按方向挑选积分器：优先原生带宽日志，其次用 IOPS×块大小推算。</summary>
    private static VolumeIntegrator BuildIntegrator(ChartDataSet data, int direction)
    {
        if (data.HasBandwidth)
        {
            var bw = FioLogParser.FilterDirection(data.BandwidthSamples, direction);
            if (bw.Count > 0)
                return VolumeIntegrator.FromBandwidth(bw);
        }

        if (data.HasIops)
        {
            var iops = FioLogParser.FilterDirection(data.IopsSamples, direction);
            if (iops.Count > 0 && iops.Any(s => s.BlockSize > 0))
                return VolumeIntegrator.FromIops(iops);
        }

        return VolumeIntegrator.Empty;
    }

    /// <summary>
    /// 按 x 排序并去掉"完全重复"的点（x、y 都相同）。
    /// 注意：同一时间戳但 y 不同的点必须保留（例如收尾窗口的两个采样），否则图表会少点。
    /// </summary>
    private static List<ChartPoint> Normalize(List<ChartPoint> points)
    {
        if (points.Count <= 1)
            return points;

        return points
            .GroupBy(p => (X: Math.Round(p.X, 9), Y: Math.Round(p.Y, 6)))
            .Select(g => g.Last())
            .OrderBy(p => p.X)
            .ToList();
    }

    private static string DirectionName(int direction) => direction switch
    {
        0 => AppText.T("读 (read)", "Read"),
        1 => AppText.T("写 (write)", "Write"),
        2 => "trim",
        3 => "sync",
        _ => $"ddir={direction}",
    };

    private static string SeriesName(int direction, YAxisMetric metric) => metric switch
    {
        YAxisMetric.Latency => direction switch
        {
            0 => AppText.T("读延迟 (read)", "Read latency"),
            1 => AppText.T("写延迟 (write)", "Write latency"),
            2 => AppText.T("trim 延迟", "Trim latency"),
            _ => $"延迟 (ddir={direction})",
        },
        _ => DirectionName(direction),
    };

    private static SeriesRole RoleOf(int direction) => direction switch
    {
        0 => SeriesRole.Read,
        1 => SeriesRole.Write,
        _ => SeriesRole.Other,
    };

    private static string? BuildEmptyMessage(ChartDataSet data, XAxisDimension x, YAxisMetric y, bool hasSeries)
    {
        if (hasSeries)
            return null;

        if (y == YAxisMetric.Iops && !data.HasIops)
            return AppText.T("缺少 IOPS 日志：请确认 fio 参数 write_iops_log 已开启，并加载 *_iops.*.log 文件。", "Missing IOPS log: enable fio option write_iops_log and load *_iops.*.log.");

        if (y == YAxisMetric.ThroughputMebibytesPerSecond && !data.HasBandwidth)
            return AppText.T("缺少带宽日志：请确认 fio 参数 write_bw_log 已开启，并加载 *_bw.*.log 文件。", "Missing bandwidth log: enable fio option write_bw_log and load *_bw.*.log.");

        if (y == YAxisMetric.Latency && !data.HasLatency)
            return AppText.T("缺少延迟日志：请确认 fio 参数 write_lat_log 已开启，并加载 *_lat.*.log 文件。", "Missing latency log: enable fio option write_lat_log and load *_lat.*.log.");

        if (x == XAxisDimension.TransferredMebibytes && !data.HasBandwidth && !data.HasIops)
            return AppText.T("无法计算累计传输量：需要 bw 日志或带块大小的 iops 日志。", "Cannot compute transferred volume: need a bandwidth log or an IOPS log with block sizes.");

        return AppText.T("没有可绘制的数据点：日志为空或格式不符。", "No plottable samples: the log is empty or malformed.");
    }

    /// <summary>一句话描述数据集，显示在状态栏。</summary>
    public static string Describe(ChartDataSet data)
    {
        if (data.TotalSamples == 0)
            return AppText.T("空数据集", "Empty dataset");

        var readPoints = data.BandwidthSamples.Concat(data.IopsSamples).Count(s => s.IsRead);
        var writePoints = data.BandwidthSamples.Concat(data.IopsSamples).Count(s => s.IsWrite);
        if (AppText.IsEnglish)
        {
            return $"{data.TotalSamples} samples · {data.DurationSeconds:0.#} s" +
                   $" · read {readPoints} / write {writePoints}" +
                   (data.IsSynthetic ? " · synthetic" : "");
        }

        return $"{data.TotalSamples} 个采样点 · 时长 {data.DurationSeconds:0.#} 秒" +
               $" · 读 {readPoints} / 写 {writePoints}" +
               (data.IsSynthetic ? " · 合成示例" : "");
    }
}
