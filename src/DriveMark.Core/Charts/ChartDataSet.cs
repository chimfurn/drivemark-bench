using DriveMark.Core.Parsing;

namespace DriveMark.Core.Charts;

/// <summary>一份可供绘图的 fio 日志数据集。</summary>
public sealed record ChartDataSet(
    string Name,
    string Description,
    bool IsSynthetic,
    IReadOnlyList<FioLogSample> BandwidthSamples,
    IReadOnlyList<FioLogSample> IopsSamples)
{
    /// <summary>延迟日志采样（fio 的 *_lat.*.log，数值单位是纳秒）。</summary>
    public IReadOnlyList<FioLogSample> LatencySamples { get; init; } = [];

    public bool HasBandwidth => BandwidthSamples.Count > 0;
    public bool HasIops => IopsSamples.Count > 0;
    public bool HasLatency => LatencySamples.Count > 0;
    public int TotalSamples => BandwidthSamples.Count + IopsSamples.Count + LatencySamples.Count;

    public static ChartDataSet Empty(string name, string description) =>
        new(name, description, false, [], []);

    /// <summary>数据集里能画出来的时长（秒），取各类日志的最大时间。</summary>
    public double DurationSeconds
    {
        get
        {
            var maxMs = Math.Max(
                BandwidthSamples.Count == 0 ? 0 : BandwidthSamples.Max(s => s.TimeMs),
                Math.Max(
                    IopsSamples.Count == 0 ? 0 : IopsSamples.Max(s => s.TimeMs),
                    LatencySamples.Count == 0 ? 0 : LatencySamples.Max(s => s.TimeMs)));
            return maxMs / 1000d;
        }
    }
}

/// <summary>
/// 从磁盘加载 fio 日志。
/// 只读取文件，绝不调用 fio —— 开发期对用户的硬盘零写入。
/// </summary>
public static class FioLogDataSetLoader
{
    private static readonly string[] BwPatterns = ["*_bw.*.log", "*_bw.log"];
    private static readonly string[] IopsPatterns = ["*_iops.*.log", "*_iops.log"];
    private static readonly string[] LatPatterns = ["*_lat.*.log", "*_lat.log"];

    /// <summary>显式指定 bw / iops / lat 日志文件。</summary>
    public static ChartDataSet FromFiles(
        string name,
        string description,
        string? bandwidthLogPath,
        string? iopsLogPath,
        bool isSynthetic = false,
        string? latencyLogPath = null)
    {
        var bw = bandwidthLogPath is not null ? FioLogParser.ParseFile(bandwidthLogPath) : [];
        var iops = iopsLogPath is not null ? FioLogParser.ParseFile(iopsLogPath) : [];
        var latency = latencyLogPath is not null ? FioLogParser.ParseFile(latencyLogPath) : [];
        return new ChartDataSet(name, description, isSynthetic, bw, iops)
        {
            LatencySamples = latency,
        };
    }

    /// <summary>
    /// 从目录里找日志：取每个类别的第一个文件。
    /// （numjobs&gt;1 会产生多份日志，聚合显示留待后续迭代。）
    /// 若目录里有 job.fio，会从中提取 rw/bs/iodepth 生成可读名称。
    /// </summary>
    public static ChartDataSet FromDirectory(
        string directory,
        string? name = null,
        string? description = null,
        bool isSynthetic = false)
    {
        if (!Directory.Exists(directory))
            return ChartDataSet.Empty(name ?? Path.GetFileName(directory), description ?? AppText.T("目录不存在", "Directory not found"));

        var bwPath = FindFirst(directory, BwPatterns);
        var iopsPath = FindFirst(directory, IopsPatterns);
        var latPath = FindFirst(directory, LatPatterns);

        var jobInfo = TryReadJobSummary(directory);
        var fileName = name
                       ?? (jobInfo is not null ? $"{jobInfo}{AppText.T("（实测）", " (measured)")}" : null)
                       ?? Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var desc = description ?? AppText.T("日志目录：", "Log directory: ") + directory;
        return FromFiles(fileName, desc, bwPath, iopsPath, isSynthetic, latPath);
    }

    /// <summary>从 job.fio 里提取 "rw bs QDiodepth" 形式的摘要（读不到就返回 null）。</summary>
    private static string? TryReadJobSummary(string directory)
    {
        try
        {
            var jobFile = Path.Combine(directory, "job.fio");
            if (!File.Exists(jobFile))
                return null;

            string? rw = null, bs = null, iodepth = null;
            foreach (var rawLine in File.ReadLines(jobFile))
            {
                var line = rawLine.Trim();
                if (line.StartsWith('#') || line.StartsWith('['))
                    continue;
                var index = line.IndexOf('=');
                if (index <= 0)
                    continue;

                var key = line[..index].Trim();
                var value = line[(index + 1)..].Trim();
                switch (key)
                {
                    case "rw": rw = value; break;
                    case "bs": bs = value; break;
                    case "iodepth": iodepth = value; break;
                }
            }

            if (rw is null && bs is null)
                return null;

            return $"{rw ?? "?"} {bs ?? "?"} QD{iodepth ?? "1"}";
        }
        catch
        {
            return null;
        }
    }
    private static string? FindFirst(string directory, string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            var files = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
            if (files.Length > 0)
                return files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).First();
        }
        return null;
    }
}
