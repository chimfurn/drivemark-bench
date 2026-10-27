using System.Globalization;

namespace DriveMark.Core.Charts;

/// <summary>数字与单位的统一格式化。图表轴标签、提示、结果表都走这里，避免各处口径不一致。</summary>
public static class Units
{
    /// <summary>IOPS 轴：不超过该值时用具体整数，超过才用 k 缩写。</summary>
    public const double IopsCompactThreshold = 10_000;

    /// <summary>IOPS：≤10k 用具体整数（8000），超过用 k/M 缩写（12.5k、1.2M）。</summary>
    public static string Iops(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return "-";
        var abs = Math.Abs(value);
        if (abs >= 1_000_000)
            return (value / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M";
        if (abs > IopsCompactThreshold)
            return (value / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        return PlainConcrete(value);
    }

    private static string FormatZeroSafe(double value, Func<double, string> formatter) =>
        Math.Abs(value) < 0.0001 ? "0" : formatter(value);

    /// <summary>
    /// IOPS 轴刻度格式（按整条轴的最大值决定，保证同一张图内风格统一）：
    /// - 轴最大值 ≤ 10,000 → 全部用具体整数（如 2500、8000，不用 k）；
    /// - 10,000 &lt; 最大值 &lt; 1,000,000 → 用 k 缩写（如 20k、37.5k，0 仍显示 0）；
    /// - ≥ 1,000,000 → 用 M 缩写。
    /// </summary>
    public static Func<double, string> IopsAxisFor(double axisMaxValue)
    {
        var abs = Math.Abs(axisMaxValue);
        if (abs <= IopsCompactThreshold)
            return PlainConcrete;
        if (abs >= 1_000_000)
            return value => FormatZeroSafe(value,
                v => (v / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M");
        return value => FormatZeroSafe(value,
            v => (v / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "k");
    }

    /// <summary>吞吐量：按 MiB/s 输入，超过 1024 自动换算成 GiB/s。</summary>
    public static string MebibytesPerSecond(double mib)
    {
        if (double.IsNaN(mib) || double.IsInfinity(mib))
            return "-";
        if (Math.Abs(mib) >= 1024)
            return (mib / 1024d).ToString("0.##", CultureInfo.InvariantCulture) + " GiB/s";
        if (Math.Abs(mib) >= 100)
            return mib.ToString("0", CultureInfo.InvariantCulture) + " MiB/s";
        return mib.ToString("0.#", CultureInfo.InvariantCulture) + " MiB/s";
    }

    /// <summary>纯数值（用于轴标签，单位写在轴标题里），大值会用 k/M 缩写。</summary>
    public static string Plain(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return "-";
        var abs = Math.Abs(value);
        if (abs >= 1_000_000)
            return (value / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M";
        if (abs >= 10_000)
            return (value / 1000d).ToString("0", CultureInfo.InvariantCulture) + "k";
        if (abs >= 1000)
            return (value / 1000d).ToString("0.##", CultureInfo.InvariantCulture) + "k";
        if (Math.Abs(value - Math.Round(value)) < 0.0001)
            return value.ToString("0", CultureInfo.InvariantCulture);
        if (abs >= 10)
            return value.ToString("0.#", CultureInfo.InvariantCulture);
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 具体数值格式：不使用 k/M 缩写（1000 就显示 1000）。
    /// 用于吞吐量轴（MiB/s）和 IOPS 小范围轴，避免"1k"这种不直观的刻度。
    /// </summary>
    public static string PlainConcrete(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return "-";
        if (Math.Abs(value - Math.Round(value)) < 0.0001)
            return value.ToString("0", CultureInfo.InvariantCulture);
        var abs = Math.Abs(value);
        if (abs >= 100)
            return value.ToString("0.#", CultureInfo.InvariantCulture);
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>IOPS 轴的紧凑标签（旧默认：始终 k 缩写；新代码优先用 IopsAxisFor）。</summary>
    public static string IopsAxis(double value) => Iops(value);

    /// <summary>吞吐量轴标签（MiB/s）：始终显示具体数值，不用 k 缩写。</summary>
    public static string ThroughputAxis(double value) => PlainConcrete(value);

    /// <summary>时间轴标签（秒）。始终用秒，避免出现 "0,10,…,50,1m" 这种混排。</summary>
    public static string SecondsAxis(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return "-";
        return Math.Abs(value - Math.Round(value)) < 0.0001
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.#", CultureInfo.InvariantCulture);
    }

    /// <summary>字节数的可读形式（1.5 GiB）。</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 0)
            return "-";
        return Mebibytes(bytes / (1024d * 1024d));
    }

    /// <summary>以 MiB 为单位输入，自动升到 GiB。</summary>
    public static string Mebibytes(double mib)
    {
        if (double.IsNaN(mib) || double.IsInfinity(mib))
            return "-";
        if (Math.Abs(mib) >= 1024)
            return (mib / 1024d).ToString("0.##", CultureInfo.InvariantCulture) + " GiB";
        return mib.ToString("0.##", CultureInfo.InvariantCulture) + " MiB";
    }

    /// <summary>时长：65.0 → 1:05。</summary>
    public static string Duration(TimeSpan ts) =>
        ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes}:{ts.Seconds:00}";

    /// <summary>百分比。</summary>
    public static string Percent(double value) =>
        double.IsNaN(value) ? "-" : value.ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
