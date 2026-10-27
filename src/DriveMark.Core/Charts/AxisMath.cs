namespace DriveMark.Core.Charts;

/// <summary>
/// 坐标轴配置：支持自动缩放，也支持完全自定义（最小/最大/步长）。
/// 这就是"自定义坐标轴"的核心：用同一套 AxisSpec 驱动 4 种轴组合，并允许手工覆盖。
/// </summary>
public sealed record AxisSpec
{
    /// <summary>是否自动根据数据计算范围（false 时优先使用下面三项）。</summary>
    public bool AutoScale { get; init; } = true;

    /// <summary>自定义最小值（null = 自动）。</summary>
    public double? ManualMin { get; init; }

    /// <summary>自定义最大值（null = 自动）。</summary>
    public double? ManualMax { get; init; }

    /// <summary>自定义刻度步长（null = 自动取"好看"的步长）。</summary>
    public double? ManualStep { get; init; }

    /// <summary>自动模式下期望的刻度数量。</summary>
    public int TargetTickCount { get; init; } = 6;

    /// <summary>是否必须从 0 开始（吞吐量/IOPS 通常从 0 起更直观）。</summary>
    public bool StartAtZero { get; init; }

    public static readonly AxisSpec Auto = new();

    public static AxisSpec AutoFromZero(int targetTicks = 6) =>
        new() { AutoScale = true, TargetTickCount = targetTicks, StartAtZero = true };

    public static AxisSpec Manual(double min, double max, double? step = null) =>
        new() { AutoScale = false, ManualMin = min, ManualMax = max, ManualStep = step };
}

/// <summary>解析后的轴范围与刻度。</summary>
public sealed record AxisRange(double Min, double Max, double Step, IReadOnlyList<double> Ticks)
{
    public double Span => Max - Min;

    /// <summary>空数据的占位范围。</summary>
    public static readonly AxisRange Empty = new(0, 1, 0.2, [0, 0.2, 0.4, 0.6, 0.8, 1]);

    /// <summary>把数值映射到 [0,1] 的归一化位置。</summary>
    public double Normalize(double value) =>
        Span <= 0 ? 0 : Math.Clamp((value - Min) / Span, 0, 1);
}

/// <summary>"好看"的刻度步长算法。</summary>
public static class NiceTicks
{
    /// <summary>
    /// 把任意步长向上取整到 1/2/2.5/5/10 × 10^n。
    /// 例：0.37→0.5，1.2→2，2.4→2.5，7→10，23→25。
    /// </summary>
    public static double NiceStep(double rawStep)
    {
        if (double.IsNaN(rawStep) || double.IsInfinity(rawStep) || rawStep <= 0)
            return 1;

        var exponent = Math.Floor(Math.Log10(rawStep));
        var pow = Math.Pow(10, exponent);
        var fraction = rawStep / pow;

        // 加一点容差，避免 2.0000000001 被抬到 2.5。
        var nice = fraction switch
        {
            <= 1.0000001 => 1d,
            <= 2.0000001 => 2d,
            <= 2.5000001 => 2.5d,
            <= 5.0000001 => 5d,
            _ => 10d,
        };
        return nice * pow;
    }

    /// <summary>按起点/步长生成刻度，最多 <paramref name="maxTicks"/> 个，防止死循环。</summary>
    public static IReadOnlyList<double> Generate(double min, double max, double step, int maxTicks = 60)
    {
        var ticks = new List<double>();
        if (step <= 0 || double.IsNaN(step) || double.IsInfinity(step) || max <= min)
        {
            ticks.Add(min);
            return ticks;
        }

        // 用整数计数避免浮点累计误差。
        var count = (int)Math.Floor((max - min) / step + 1e-9) + 1;
        count = Math.Clamp(count, 1, maxTicks);
        for (var i = 0; i < count; i++)
            ticks.Add(Minify(min + i * step));
        return ticks;
    }

    /// <summary>去掉浮点尾差（0.30000000000000004 → 0.3）。</summary>
    public static double Minify(double value) => Math.Round(value, 10, MidpointRounding.AwayFromZero);
}

/// <summary>把 <see cref="AxisSpec"/> + 数据解析成可绘制的 <see cref="AxisRange"/>。</summary>
public static class AxisResolver
{
    public static AxisRange Resolve(AxisSpec spec, IEnumerable<double> rawValues)
    {
        var values = rawValues.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).ToList();
        if (values.Count == 0)
            return BuildEmptyRange(spec);

        var dataMin = values.Min();
        var dataMax = values.Max();

        if (spec.StartAtZero && spec.AutoScale)
            dataMin = Math.Min(0, dataMin);

        // 数据全等（例如恒定速率）时人为撑开一个范围。
        if (Math.Abs(dataMax - dataMin) < 1e-12)
        {
            var pad = Math.Abs(dataMin) > 1e-9 ? Math.Abs(dataMin) * 0.1 : 1;
            dataMin -= pad;
            dataMax += pad;
            if (spec.StartAtZero)
                dataMin = 0;
        }

        // 手动范围优先；缺哪端补哪端。
        var min = spec.ManualMin ?? dataMin;
        var max = spec.ManualMax ?? dataMax;

        // 允许"只给步长"的自定义：范围仍按数据，但刻度步长固定。
        double step;
        if (spec.ManualStep is { } manualStep && manualStep > 0)
        {
            step = manualStep;
            if (spec.AutoScale)
            {
                // 步长固定时把边界对齐到步长格点，标签才好看。
                min = Math.Floor(min / step) * step;
                max = Math.Ceiling(max / step) * step;
            }
        }
        else
        {
            var target = Math.Max(2, spec.TargetTickCount);
            step = NiceTicks.NiceStep((max - min) / target);
            if (spec.AutoScale)
            {
                min = Math.Floor(min / step) * step;
                max = Math.Ceiling(max / step) * step;
            }
        }

        // 边界守护：范围非法时给出可绘制的最小范围。
        if (max <= min)
        {
            step = step > 0 ? step : 1;
            max = min + step;
        }

        // 对齐到步长后可能溢出成相等（浮点），再兜一次。
        if (max <= min)
            max = min + Math.Max(step, Math.Abs(min) * 1e-6 + 1e-9);

        min = NiceTicks.Minify(min);
        max = NiceTicks.Minify(max);

        var ticks = NiceTicks.Generate(min, max, step);
        return new AxisRange(min, max, NiceTicks.Minify(step), ticks);
    }

    private static AxisRange BuildEmptyRange(AxisSpec spec)
    {
        if (!spec.AutoScale && spec.ManualMin is { } mn && spec.ManualMax is { } mx && mx > mn)
        {
            var st = spec.ManualStep is { } ms && ms > 0
                ? ms
                : NiceTicks.NiceStep((mx - mn) / Math.Max(2, spec.TargetTickCount));
            return new AxisRange(mn, mx, st, NiceTicks.Generate(mn, mx, st));
        }
        return AxisRange.Empty;
    }
}
