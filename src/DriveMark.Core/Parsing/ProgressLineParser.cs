using System.Globalization;
using System.Text.RegularExpressions;

namespace DriveMark.Core.Parsing;

/// <summary>
/// fio 实时进度行的一条采样。行格式（本工作区实测，见 docs/bench_samples/live-progress-sample.utf8.txt）：
/// <code>
/// Jobs: 1 (f=2): [r(1)][27.3%][r=379MiB/s][r=97.1k IOPS][eta 00m:08s]
/// </code>
/// 写操作为 [w=...]。这条通道用于实时曲线；精确结果请读 JSON 或 bw/iops 日志。
/// </summary>
public sealed record ProgressSample(
    string? JobName,
    double? Percent,
    double? ReadMebibytesPerSecond,
    double? ReadIops,
    double? WriteMebibytesPerSecond,
    double? WriteIops,
    TimeSpan? Eta);

/// <summary>解析 fio 实时进度行。</summary>
public static class ProgressLineParser
{
    // [27.3%]
    private static readonly Regex PercentRegex = new(@"\[\s*(\d+(?:\.\d+)?)\s*%\s*\]", RegexOptions.Compiled);

    // [r=379MiB/s] / [w=1.2GiB/s] / [r=500B/s]
    private static readonly Regex ReadRateRegex =
        new(@"\[r=([0-9.]+)\s*([KMGTP]?)i?B/s\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WriteRateRegex =
        new(@"\[w=([0-9.]+)\s*([KMGTP]?)i?B/s\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // [r=97.1k IOPS] / [r=500 IOPS]
    private static readonly Regex ReadIopsRegex =
        new(@"\[r=([0-9.]+)\s*([kK])?\s*IOPS\]", RegexOptions.Compiled);

    private static readonly Regex WriteIopsRegex =
        new(@"\[w=([0-9.]+)\s*([kK])?\s*IOPS\]", RegexOptions.Compiled);

    // eta 00m:08s
    private static readonly Regex EtaRegex = new(@"eta\s+(\d+)m:(\d+)s", RegexOptions.Compiled);

    // 行首任务名：random-read-4k-qd16: (f=2): ...
    private static readonly Regex JobNameRegex = new(@"^([^:\[\]]+?):\s*\(", RegexOptions.Compiled);

    /// <summary>尝试解析一行进度。没有任何可识别信息（进度/速率）时返回 false。</summary>
    public static bool TryParse(string? line, out ProgressSample sample)
    {
        sample = new ProgressSample(null, null, null, null, null, null, null);
        if (string.IsNullOrWhiteSpace(line))
            return false;

        double? percent = null;
        var m = PercentRegex.Match(line);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
            percent = p;

        var readRate = ParseRate(line, ReadRateRegex);
        var writeRate = ParseRate(line, WriteRateRegex);
        var readIops = ParseIops(line, ReadIopsRegex);
        var writeIops = ParseIops(line, WriteIopsRegex);

        // 完全没有可用指标的行（如 "Jobs: 1 (f=2)"）不产出采样。
        if (percent is null && readRate is null && writeRate is null && readIops is null && writeIops is null)
            return false;

        TimeSpan? eta = null;
        var em = EtaRegex.Match(line);
        if (em.Success &&
            int.TryParse(em.Groups[1].Value, out var minutes) &&
            int.TryParse(em.Groups[2].Value, out var seconds))
            eta = TimeSpan.FromSeconds(minutes * 60 + seconds);

        string? jobName = null;
        var jm = JobNameRegex.Match(line);
        if (jm.Success)
            jobName = jm.Groups[1].Value.Trim();

        sample = new ProgressSample(jobName, percent, readRate, readIops, writeRate, writeIops, eta);
        return true;
    }

    /// <summary>解析整段输出文本，返回全部进度采样。</summary>
    public static IReadOnlyList<ProgressSample> ParseAll(TextReader reader)
    {
        var list = new List<ProgressSample>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (TryParse(line, out var sample))
                list.Add(sample);
        }
        return list;
    }

    public static IReadOnlyList<ProgressSample> ParseAll(string text)
    {
        using var reader = new StringReader(text);
        return ParseAll(reader);
    }

    private static double? ParseRate(string line, Regex regex)
    {
        var m = regex.Match(line);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;
        var unit = m.Groups[2].Value.ToUpperInvariant();
        var mib = unit switch
        {
            "" => value / (1024d * 1024d), // B/s
            "K" => value / 1024d,          // KiB/s
            "M" => value,                  // MiB/s
            "G" => value * 1024d,          // GiB/s
            "T" => value * 1024d * 1024d,
            "P" => value * 1024d * 1024d * 1024d,
            _ => value,
        };
        return mib;
    }

    private static double? ParseIops(string line, Regex regex)
    {
        var m = regex.Match(line);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;
        return m.Groups[2].Success ? value * 1000d : value;
    }
}
