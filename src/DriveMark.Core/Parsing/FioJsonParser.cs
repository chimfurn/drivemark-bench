using System.Text.Json;

namespace DriveMark.Core.Parsing;

/// <summary>一个方向的汇总结果（取自 fio JSON 的 read/write 段）。</summary>
public sealed record FioDirectionResult(
    double BandwidthKiBPerSecond,
    double BandwidthBytesPerSecond,
    double Iops,
    long TotalIos,
    long IoBytes,
    double SlatMeanNs,
    double ClatMeanNs,
    double LatMeanNs,
    double ClatP99Ns,
    double ClatP999Ns)
{
    /// <summary>带宽（MiB/s），由 bw_bytes 精确换算。</summary>
    public double MebibytesPerSecond => BandwidthBytesPerSecond / (1024d * 1024d);

    /// <summary>平均总延迟（微秒）。</summary>
    public double LatencyMeanMicroseconds => LatMeanNs / 1000d;
}

/// <summary>单个 job 的结果。</summary>
public sealed record FioJobResult(string JobName, int Error, FioDirectionResult? Read, FioDirectionResult? Write);

/// <summary>一次 fio 运行的结构化结果。</summary>
public sealed record FioJsonResult(string FioVersion, string? TimestampText, IReadOnlyList<FioJobResult> Jobs)
{
    public FioDirectionResult? FirstRead => Jobs.Select(j => j.Read).FirstOrDefault(r => r is not null);
    public FioDirectionResult? FirstWrite => Jobs.Select(j => j.Write).FirstOrDefault(r => r is not null);
}

/// <summary>
/// 解析 fio 的 JSON 输出。
/// 关键坑位（本工作区实测）：fio 会在 JSON 前面输出告警文本，例如
/// <c>fio: this platform does not support process shared mutexes, ...</c>，
/// 因此必须先定位第一个 '{' 再解析，绝不能直接反序列化整个文件。
/// </summary>
public static class FioJsonParser
{
    /// <summary>把 fio 输出裁剪为纯 JSON（去掉首个 '{' 之前的脏文本）。</summary>
    public static string StripPreamble(string raw)
    {
        var idx = raw.IndexOf('{');
        return idx < 0 ? throw new InvalidDataException("fio 输出中找不到 JSON 起始的 '{'") : raw[idx..];
    }

    /// <summary>解析文本；失败抛 <see cref="InvalidDataException"/> 并带可读信息。</summary>
    public static FioJsonResult Parse(string raw)
    {
        var json = StripPreamble(raw);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var version = TryString(root, "fio version") ?? "unknown";
        var time = TryString(root, "time");

        var jobs = new List<FioJobResult>();
        if (root.TryGetProperty("jobs", out var jobsEl) && jobsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var job in jobsEl.EnumerateArray())
            {
                var name = TryString(job, "jobname") ?? "job";
                var error = TryInt(job, "error") ?? 0;
                var read = ParseDirection(job, "read");
                var write = ParseDirection(job, "write");
                jobs.Add(new FioJobResult(name, error, read, write));
            }
        }

        return new FioJsonResult(version, time, jobs);
    }

    public static FioJsonResult ParseFile(string path) =>
        Parse(File.ReadAllText(path));

    private static FioDirectionResult? ParseDirection(JsonElement job, string name)
    {
        if (!job.TryGetProperty(name, out var dir) || dir.ValueKind != JsonValueKind.Object)
            return null;

        var io = dir.TryGetProperty("io_bytes", out var ioEl) && ioEl.TryGetInt64(out var ioBytes) ? ioBytes : 0;
        if (io == 0 && !dir.TryGetProperty("iops", out _))
            return null;

        return new FioDirectionResult(
            BandwidthKiBPerSecond: TryDouble(dir, "bw") ?? 0,
            BandwidthBytesPerSecond: TryDouble(dir, "bw_bytes") ?? 0,
            Iops: TryDouble(dir, "iops") ?? 0,
            TotalIos: TryLong(dir, "total_ios") ?? 0,
            IoBytes: io,
            SlatMeanNs: TryNested(dir, "slat_ns", "mean"),
            ClatMeanNs: TryNested(dir, "clat_ns", "mean"),
            LatMeanNs: TryNested(dir, "lat_ns", "mean"),
            ClatP99Ns: TryPercentile(dir, "99.000000"),
            ClatP999Ns: TryPercentile(dir, "99.900000"));
    }

    private static double TryPercentile(JsonElement dir, string key)
    {
        if (!dir.TryGetProperty("clat_ns", out var clat) || !clat.TryGetProperty("percentile", out var pcts))
            return double.NaN;
        return pcts.TryGetProperty(key, out var el) && el.TryGetDouble(out var v) ? v : double.NaN;
    }

    private static string? TryString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? TryInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : null;

    private static long? TryLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.TryGetInt64(out var v) ? v : null;

    private static double? TryDouble(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.TryGetDouble(out var v) ? v : null;

    private static double TryNested(JsonElement el, string obj, string field)
    {
        if (!el.TryGetProperty(obj, out var o) || o.ValueKind != JsonValueKind.Object)
            return double.NaN;
        return o.TryGetProperty(field, out var f) && f.TryGetDouble(out var v) ? v : double.NaN;
    }
}
