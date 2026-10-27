namespace DriveMark.Core.Options;

/// <summary>一个内置测试档位。</summary>
public sealed record FioPreset(
    string Name,
    string Description,
    IReadOnlyDictionary<string, string> Values)
{
    /// <summary>是否包含写入操作（界面据此显示红色安全提示）。</summary>
    public bool WritesData =>
        Values.TryGetValue("rw", out var rw) &&
        (rw.Contains("write", StringComparison.OrdinalIgnoreCase) ||
         rw is "rw" or "randrw" or "trimwrite" or "randtrimwrite");
}

/// <summary>
/// 内置测试档位。设计原则：能直接产出图表所需日志（write_bw_log / write_iops_log），
/// 且默认参数全部来自 fio 源码中核对过的合法范围。
/// </summary>
public static class FioPresets
{
    private static Dictionary<string, string> BaseSafe(string rw, string bs, int iodepth) => new()
    {
        ["rw"] = rw,
        ["bs"] = bs,
        ["ioengine"] = "windowsaio",
        ["direct"] = "1",
        ["iodepth"] = iodepth.ToString(),
        ["numjobs"] = "1",
        ["thread"] = "1",
        ["time_based"] = "1",
        ["runtime"] = "10s",
        ["ramp_time"] = "2s",
        ["size"] = "1g",
        ["group_reporting"] = "1",
        ["invalidate"] = "1",
        ["norandommap"] = "1",
        // 图表数据链路：bw 日志给“吞吐量”，iops 日志给“IOPS”，lat 日志给“延迟”，1 秒采样
        ["write_bw_log"] = "fio-gui-bw",
        ["write_iops_log"] = "fio-gui-iops",
        ["write_lat_log"] = "fio-gui-lat",
        ["log_avg_msec"] = "1000",
        ["bwavgtime"] = "1000",
        ["iopsavgtime"] = "1000",
    };

    public static readonly IReadOnlyList<FioPreset> All =
    [
        new("图表采集·4K 随机读", "最贴近日常体验；输出 bw/iops 日志供曲线绘制、不写盘。",
            BaseSafe("randread", "4k", 32)),
        new("图表采集·4K 随机写", "小文件随机写能力；会真实写入磁盘，务必指向测试盘。",
            BaseSafe("randwrite", "4k", 32)),
        new("图表采集·1M 顺序读", "顺序读带宽；输出日志供曲线绘制。",
            BaseSafe("read", "1m", 8)),
        new("图表采集·混合 70/30", "70% 读 / 30% 写的混合负载；会真实写入磁盘。",
            new Dictionary<string, string>(BaseSafe("randrw", "4k", 32))
            {
                ["rwmixread"] = "70",
            }),
        new("顺序读 1M Q8", "纯带宽测试，不写盘。",
            new Dictionary<string, string>(BaseSafe("read", "1m", 8))
            {
                ["write_bw_log"] = "", ["write_iops_log"] = "", ["write_lat_log"] = "",
            }),
        new("随机读 4K Q1", "单队列随机读，延迟最敏感。",
            new Dictionary<string, string>(BaseSafe("randread", "4k", 1))
            {
                ["write_bw_log"] = "", ["write_iops_log"] = "", ["write_lat_log"] = "",
            }),
        new("随机读 4K Q32", "压满 IOPS 上限。",
            new Dictionary<string, string>(BaseSafe("randread", "4k", 32))
            {
                ["write_bw_log"] = "", ["write_iops_log"] = "", ["write_lat_log"] = "",
            }),
        new("延迟测试 4K Q1", "30 秒随机读，观察长尾延迟；不写盘。",
            new Dictionary<string, string>(BaseSafe("randread", "4k", 1))
            {
                ["runtime"] = "30s",
                ["ramp_time"] = "5s",
                ["write_bw_log"] = "", ["write_iops_log"] = "", ["write_lat_log"] = "",
            }),
    ];

    public static FioPreset? Find(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>默认配置：只读、带图表日志、不指向任何真实文件。</summary>
    public static FioJobConfig CreateDefault()
    {
        // 注意：ApplyPreset 返回新配置，必须返回它，否则默认参数会丢失。
        var preset = All[0]; // 4K 随机读（不写盘）
        return new FioJobConfig { JobName = "fio-gui-job" }.ApplyPreset(preset);
    }
}
