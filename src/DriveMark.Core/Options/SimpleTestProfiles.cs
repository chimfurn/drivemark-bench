using DriveMark.Core.Options;

namespace DriveMark.Core.Options;

/// <summary>面向普通用户的"简单模式"测试项目。</summary>
public sealed record SimpleTestProfile(
    string Name,
    string Hint,
    IReadOnlyDictionary<string, string> Values)
{
    public bool WritesData => FioValueParser.IsWriteMode(Values.GetValueOrDefault("rw"));

    public override string ToString() => Name;
}

/// <summary>简单模式下的可选值（测试项目 / 文件大小 / 测试时长 / 采样间隔）。</summary>
public static class SimpleTestProfiles
{
    /// <summary>测试项目（普通用户只需要在这几个里选一个）。</summary>
    public static readonly IReadOnlyList<SimpleTestProfile> All =
    [
        new("随机读（推荐）", "4K 随机读，最接近日常使用感受", new Dictionary<string, string>
        {
            ["rw"] = "randread",
            ["bs"] = "4k",
            ["iodepth"] = "32",
        }),
        new("顺序读", "1M 大块顺序读，看最高读取速度", new Dictionary<string, string>
        {
            ["rw"] = "read",
            ["bs"] = "1m",
            ["iodepth"] = "8",
        }),
        new("随机写", "4K 随机写，小文件写入能力（会写盘）", new Dictionary<string, string>
        {
            ["rw"] = "randwrite",
            ["bs"] = "4k",
            ["iodepth"] = "32",
        }),
        new("顺序写", "1M 大块顺序写，看最高写入速度（会写盘）", new Dictionary<string, string>
        {
            ["rw"] = "write",
            ["bs"] = "1m",
            ["iodepth"] = "8",
        }),
        new("混合读写", "70% 读 + 30% 写（会写盘）", new Dictionary<string, string>
        {
            ["rw"] = "randrw",
            ["bs"] = "4k",
            ["iodepth"] = "32",
            ["rwmixread"] = "70",
        }),
    ];

    public static SimpleTestProfile? Find(string name) =>
        All.FirstOrDefault(profile => string.Equals(profile.Name, name, StringComparison.Ordinal));

    /// <summary>默认测试项目（随机读，不写盘）。</summary>
    public static SimpleTestProfile Default => All[0];

    /// <summary>界面上的文件大小预设（可编辑，也可自己输入）。</summary>
    public static readonly IReadOnlyList<(string Label, string Value)> SizePresets =
    [
        ("128 MB", "128m"),
        ("256 MB", "256m"),
        ("512 MB", "512m"),
        ("1 GB", "1g"),
        ("2 GB", "2g"),
        ("4 GB", "4g"),
    ];

    /// <summary>界面上的块大小预设（默认 4 KB，可编辑）。</summary>
    public static readonly IReadOnlyList<(string Label, string Value)> BlockSizePresets =
    [
        ("512 B", "512"),
        ("4 KB", "4k"),
        ("8 KB", "8k"),
        ("16 KB", "16k"),
        ("32 KB", "32k"),
        ("64 KB", "64k"),
        ("128 KB", "128k"),
        ("1 MB", "1m"),
    ];

    /// <summary>界面上的测试时长预设（可编辑，例如自己输入 1800 秒）。</summary>
    public static readonly IReadOnlyList<(string Label, string Value)> DurationPresets =
    [
        ("5 秒", "5s"),
        ("10 秒", "10s"),
        ("30 秒", "30s"),
        ("60 秒", "60s"),
    ];

    /// <summary>
    /// 采样间隔预设（可编辑）。fio 的采样窗口 = min(log_avg_msec, bwavgtime/iopsavgtime)，
    /// 所以三个参数会一起设置。默认 10ms（约每秒 100 次）。
    /// </summary>
    public static readonly IReadOnlyList<(string Label, int Milliseconds)> SamplingPresets =
    [
        ("10 毫秒", 10),
        ("50 毫秒", 50),
        ("100 毫秒", 100),
        ("500 毫秒", 500),
        ("1 秒", 1000),
    ];

    public const int DefaultSamplingMilliseconds = 10;
    public const string DefaultSize = "256m";
    public const string DefaultRuntime = "10s";
    public const string DefaultBlockSize = "4k";
    public const string DefaultQueueDepth = "32";
    public const string DefaultJobCount = "1";

    /// <summary>队列深度（iodepth）预设：发给硬盘的并发请求数。</summary>
    public static readonly IReadOnlyList<(string Label, string Value)> QueueDepthPresets =
    [
        ("1", "1"),
        ("2", "2"),
        ("4", "4"),
        ("8", "8"),
        ("16", "16"),
        ("32", "32"),
        ("64", "64"),
        ("128", "128"),
    ];

    /// <summary>队列数 / 任务数（numjobs）预设：同时跑的 fio 任务个数。</summary>
    public static readonly IReadOnlyList<(string Label, string Value)> JobCountPresets =
    [
        ("1", "1"),
        ("2", "2"),
        ("4", "4"),
        ("8", "8"),
        ("16", "16"),
    ];

    /// <summary>把采样间隔写入配置（fio 需要三个参数同时设置）。</summary>
    public static void ApplySampling(FioJobConfig config, int milliseconds)
    {
        config.TrySet("log_avg_msec", milliseconds.ToString());
        config.TrySet("bwavgtime", milliseconds.ToString());
        config.TrySet("iopsavgtime", milliseconds.ToString());
    }

    /// <summary>构造简单模式的默认配置：随机读 / 256MB / 10 秒 / 10ms 采样。</summary>
    public static FioJobConfig CreateDefaultConfig()
    {
        var config = new FioJobConfig { JobName = "fio-gui-job" };
        ApplyProfile(config, Default);
        config.TrySet("bs", DefaultBlockSize);
        config.TrySet("ioengine", "windowsaio");
        config.TrySet("direct", "1");
        config.TrySet("numjobs", "1");
        config.TrySet("thread", "1");
        config.TrySet("time_based", "1");
        config.TrySet("runtime", DefaultRuntime);
        config.TrySet("ramp_time", "1s");
        config.TrySet("size", DefaultSize);
        config.TrySet("group_reporting", "1");
        config.TrySet("invalidate", "1");
        config.TrySet("norandommap", "1");
        config.TrySet("write_bw_log", "fio-gui-bw");
        config.TrySet("write_iops_log", "fio-gui-iops");
        config.TrySet("write_lat_log", "fio-gui-lat");
        ApplySampling(config, DefaultSamplingMilliseconds);
        return config;
    }

    /// <summary>把测试项目写入配置（清除可能残留的混合比例）。</summary>
    public static void ApplyProfile(FioJobConfig config, SimpleTestProfile profile)
    {
        config.Reset("rwmixread");
        foreach (var (key, value) in profile.Values)
            config.TrySet(key, value);
    }
}
