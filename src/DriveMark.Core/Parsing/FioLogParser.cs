namespace DriveMark.Core.Parsing;

/// <summary>fio 日志文件类型（由文件名后缀推断）。</summary>
public enum FioLogKind
{
    Unknown = 0,
    /// <summary><c>&lt;name&gt;_bw.&lt;job&gt;.log</c>：每行值 = 带宽 KiB/s。</summary>
    Bandwidth,
    /// <summary><c>&lt;name&gt;_iops.&lt;job&gt;.log</c>：每行值 = IOPS。</summary>
    Iops,
    /// <summary><c>&lt;name&gt;_slat/_clat/_lat.&lt;job&gt;.log</c>：每行值 = 纳秒延迟。</summary>
    Latency,
}

/// <summary>
/// fio 日志中的一行采样。
/// 格式来自本工作区 fio-3.37 源码 iolog.c 的 flush_samples()：
/// 无 log_offset 时 <c>"%llu, %lld, %u, %llu, %u"</c> =
/// 时间(ms), 数值, 方向(ddir), 块大小(bs), 优先级(prio)；
/// 开启 log_offset 时多一列偏移。
/// ddir 取值见 io_ddir.h：0=read, 1=write, 2=trim, 3=sync。
/// </summary>
public readonly record struct FioLogSample(
    long TimeMs,
    double Value,
    int Direction,
    long BlockSize,
    long Priority)
{
    /// <summary>方向名：read / write / trim / sync / unknown。</summary>
    public string DirectionName => Direction switch
    {
        0 => "read",
        1 => "write",
        2 => "trim",
        3 => "sync",
        _ => "unknown",
    };

    public bool IsRead => Direction == 0;
    public bool IsWrite => Direction == 1;
}

/// <summary>
/// fio 文本日志解析器。只按源码定义的格式解析，不调用 fio。
/// </summary>
public static class FioLogParser
{
    /// <summary>按文件名推断日志类型。</summary>
    public static FioLogKind KindFromFileName(string fileName)
    {
        var name = Path.GetFileName(fileName).ToLowerInvariant();
        if (name.Contains("_bw."))
            return FioLogKind.Bandwidth;
        if (name.Contains("_iops."))
            return FioLogKind.Iops;
        if (name.Contains("_slat.") || name.Contains("_clat.") || name.Contains("_lat."))
            return FioLogKind.Latency;
        return FioLogKind.Unknown;
    }

    /// <summary>解析一段 fio 日志文本。无法解析的行会被跳过（不抛异常）。</summary>
    public static IReadOnlyList<FioLogSample> Parse(TextReader reader)
    {
        var samples = new List<FioLogSample>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (TryParseLine(line, out var sample))
                samples.Add(sample);
        }
        return samples;
    }

    /// <summary>解析一行；格式不符返回 false。</summary>
    public static bool TryParseLine(string? line, out FioLogSample sample)
    {
        sample = default;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var parts = line.Split(',', StringSplitOptions.TrimEntries);
        // 至少 4 列：时间, 数值, 方向, 块大小（第 5 列优先级可能缺失）。
        if (parts.Length < 4)
            return false;

        if (!long.TryParse(parts[0], out var timeMs))
            return false;

        // 数值列可能是带小数的历史版本，也可能是负值（PRId64），统一按 double 解析。
        if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            return false;

        if (!int.TryParse(parts[2], out var direction))
            return false;

        long blockSize = 0;
        if (parts.Length > 3 && !long.TryParse(parts[3], out blockSize))
            blockSize = 0;

        long priority = 0;
        if (parts.Length > 4)
        {
            var prioText = parts[4];
            if (prioText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(prioText[2..], System.Globalization.NumberStyles.HexNumber, null, out var hexPrio))
                priority = hexPrio;
            else
                long.TryParse(prioText, out priority);
        }

        sample = new FioLogSample(timeMs, value, direction, blockSize, priority);
        return true;
    }

    /// <summary>读取并解析日志文件；文件不存在时返回空列表。</summary>
    public static IReadOnlyList<FioLogSample> ParseFile(string path)
    {
        if (!File.Exists(path))
            return [];
        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        return Parse(reader);
    }

    /// <summary>
    /// 把同方向的多行合并成按时间排序的序列。
    /// </summary>
    public static IReadOnlyList<FioLogSample> FilterDirection(
        IEnumerable<FioLogSample> samples, int direction) =>
        samples.Where(s => s.Direction == direction).OrderBy(s => s.TimeMs).ToList();

    /// <summary>日志中出现的所有方向（升序）。</summary>
    public static IReadOnlyList<int> DistinctDirections(IEnumerable<FioLogSample> samples) =>
        samples.Select(s => s.Direction).Distinct().OrderBy(d => d).ToList();
}
