using DriveMark.Core.Options;

namespace DriveMark.Core.Options;

/// <summary>
/// fio 参数目录。每一项都标注了本工作区 fio-3.37 源码中的出处行号，
/// 便于随时核对；不依赖运行 fio（<c>--cmdhelp</c> 也不调用）。
/// </summary>
public static class FioOptionCatalog
{
    public const string CatTarget = "目标与文件";
    public const string CatIo = "IO 模式";
    public const string CatRuntime = "运行时";
    public const string CatLog = "日志与图表数据";
    public const string CatLatency = "延迟统计";
    public const string CatCli = "命令行（不进 job 文件）";

    private static readonly string[] RwChoices =
    [
        "read", "write", "trim", "randread", "randwrite", "randtrim",
        "rw", "randrw", "trimwrite", "randtrimwrite",
    ];

    private static readonly string[] IoEngineChoices =
    [
        "windowsaio", "sync", "psync", "vsync", "mmap", "null",
    ];

    private static readonly string[] RandomDistributionChoices =
    [
        "random", "zipf", "pareto", "normal", "zoned", "zoned_abs",
    ];

    private static readonly string[] OutputFormatChoices =
    [
        "normal", "json", "json+", "terse",
    ];

    private static readonly string[] EtaChoices = ["auto", "always", "never"];

    /// <summary>全部参数定义（顺序即 job 文件中的书写顺序）。</summary>
    public static readonly IReadOnlyList<FioOptionDefinition> All =
    [
        // ---------------- 目标与文件 ----------------
        new()
        {
            Name = "filename", DisplayName = "测试文件路径", Type = FioOptionType.String,
            Help = "fio 读写的目标文件。注意：实测 fio 在 Windows 上无法打开含中文等非 ASCII 字符的路径（err=2 filesetup.c open），请使用纯 ASCII 路径。",
            Category = CatTarget, Source = "options.c:1865",
        },
        new()
        {
            Name = "directory", DisplayName = "测试目录", Type = FioOptionType.String,
            Help = "在指定目录下创建测试文件；与 filename 二选一。同样要求纯 ASCII 路径。",
            Category = CatTarget, Source = "options.c:1877",
        },
        new()
        {
            Name = "size", DisplayName = "测试总大小", Type = FioOptionType.SizeOrTime, DefaultValue = "",
            Help = "本次任务访问的数据总量，如 1g、256m。图表要看到稳定趋势，建议不小于 1g。",
            Category = CatTarget, Source = "options.c:2299",
        },
        new()
        {
            Name = "nrfiles", DisplayName = "文件个数", Type = FioOptionType.Integer, DefaultValue = "1", Min = 1,
            Help = "把任务拆分到多少个文件上（FIO_OPT_INT，minval=1）。",
            Category = CatTarget, Source = "options.c:2629",
        },
        new()
        {
            Name = "filesize", DisplayName = "单文件大小", Type = FioOptionType.SizeOrTime,
            Help = "每个文件的大小；不填则按 size 均分。",
            Category = CatTarget, Source = "options.c:2331",
        },
        new()
        {
            Name = "overwrite", DisplayName = "允许覆盖已有数据", Type = FioOptionType.Bool, DefaultValue = "0",
            Help = "写测试时是否覆盖文件既有内容。默认 0，fio 不会覆盖已有数据。",
            Category = CatTarget, Source = "options.c:2881", Risk = FioRiskLevel.WritesData,
        },
        new()
        {
            Name = "unlink", DisplayName = "结束后删除文件", Type = FioOptionType.Bool, DefaultValue = "0",
            Help = "任务结束后删除测试文件。注意：实测 fio 的 unlink 在 Windows 上并不可靠（1GB 测试后仍残留 512MB 文件），不要只依赖它清理。",
            Category = CatTarget, Source = "options.c:4366", Risk = FioRiskLevel.Destructive,
        },
        new()
        {
            Name = "create_on_open", DisplayName = "打开时创建文件", Type = FioOptionType.Bool, DefaultValue = "0",
            Help = "是否在打开文件时才创建，而不是预先创建并预分配空间。",
            Category = CatTarget, Source = "options.c:4188",
        },

        // ---------------- IO 模式 ----------------
        new()
        {
            Name = "rw", DisplayName = "读写模式", Type = FioOptionType.Enum, DefaultValue = "read",
            Choices = RwChoices,
            Help = "IO 方向：顺序 read/write、随机 randread/randwrite、混合 rw/randrw 等。写模式会产生真实磁盘写入。",
            Category = CatIo, Source = "options.c:1946", Risk = FioRiskLevel.WritesData,
        },
        new()
        {
            Name = "bs", DisplayName = "块大小", Type = FioOptionType.SizeOrTime, DefaultValue = "4096",
            Help = "每次 IO 的块大小，如 4k、128k、1m。先用 direct=1 时需与设备扇区/擦除块对齐。",
            Category = CatIo, Source = "options.c:2410",
        },
        new()
        {
            Name = "ioengine", DisplayName = "IO 引擎", Type = FioOptionType.Enum, DefaultValue = "windowsaio",
            Choices = IoEngineChoices,
            Help = "本机 fio --enghelp 实测可用集合中的常用项；Windows 默认 windowsaio（异步）。",
            Category = CatIo, Source = "options.c:2026",
        },
        new()
        {
            Name = "direct", DisplayName = "直接 IO（绕过缓存）", Type = FioOptionType.Bool, DefaultValue = "0",
            Help = "1 = O_DIRECT，绕过系统缓存测真实盘速（关键指标，建议开）；0 = 带缓存可能虚高。",
            Category = CatIo, Source = "options.c:2848",
        },
        new()
        {
            Name = "iodepth", DisplayName = "队列深度", Type = FioOptionType.Integer, DefaultValue = "1", Min = 1,
            Help = "同时在飞的 IO 数量（FIO_OPT_INT，minval=1），异步引擎下才有意义。",
            Category = CatIo, Source = "options.c:2202",
        },
        new()
        {
            Name = "numjobs", DisplayName = "任务数", Type = FioOptionType.Integer, DefaultValue = "1", Min = 1,
            Help = "把该任务复制成多少份并发跑（多线程/多进程）。",
            Category = CatIo, Source = "options.c:2902",
        },
        new()
        {
            Name = "rwmixread", DisplayName = "混合读占比(%)", Type = FioOptionType.Integer, DefaultValue = "50", Min = 0, Max = 100,
            Help = "rw=rw/randrw 时读操作百分比，0–100。",
            Category = CatIo, Source = "options.c:3755",
        },
        new()
        {
            Name = "random_distribution", DisplayName = "随机分布", Type = FioOptionType.Enum, DefaultValue = "random",
            Choices = RandomDistributionChoices,
            Help = "随机偏移的分布模型。",
            Category = CatIo, Source = "options.c:2570",
        },
        new()
        {
            Name = "norandommap", DisplayName = "不维护随机映射表", Type = FioOptionType.StringSet,
            Help = "随机读写时不再维护已访问块映射，允许重复读同一块，可降低 CPU 开销。",
            Category = CatIo, Source = "options.c:2521",
        },
        new()
        {
            Name = "fsync", DisplayName = "每 N 个块 fsync", Type = FioOptionType.Integer, DefaultValue = "0", Min = 0,
            Help = "每写入 N 个块执行一次 fsync；0 表示不主动刷盘。",
            Category = CatIo, Source = "options.c:2778", Risk = FioRiskLevel.WritesData,
        },
        new()
        {
            Name = "invalidate", DisplayName = "运行前丢弃缓存", Type = FioOptionType.Bool, DefaultValue = "1",
            Help = "任务开始前 invalidate 文件缓存，避免上次残留数据影响结果。",
            Category = CatIo, Source = "options.c:4100",
        },

        // ---------------- 运行时 ----------------
        new()
        {
            Name = "runtime", DisplayName = "运行时长", Type = FioOptionType.SizeOrTime, DefaultValue = "0", Unit = "s",
            Help = "运行多少时间后停止，如 10s、500ms；0 表示不限制。",
            Category = CatRuntime, Source = "options.c:2926",
        },
        new()
        {
            Name = "time_based", DisplayName = "按时长运行", Type = FioOptionType.StringSet,
            Help = "配合 runtime：数据量跑完后继续循环，直到时长达标。做限时测速必须打开。",
            Category = CatRuntime, Source = "options.c:2939",
        },
        new()
        {
            Name = "ramp_time", DisplayName = "预热时间", Type = FioOptionType.SizeOrTime, Unit = "s",
            Help = "正式计时前的预热时间，不计入统计（缓存/降速稳定）。",
            Category = CatRuntime, Source = "options.c:2957",
        },
        new()
        {
            Name = "loops", DisplayName = "循环次数", Type = FioOptionType.Integer, DefaultValue = "1", Min = 1,
            Help = "任务重复执行几遍。",
            Category = CatRuntime, Source = "options.c:2891",
        },
        new()
        {
            Name = "group_reporting", DisplayName = "分组汇总报告", Type = FioOptionType.StringSet,
            Help = "numjobs>1 时把同组结果汇总输出，而不是每任务一份。",
            Category = CatRuntime, Source = "options.c:4719",
        },
        new()
        {
            Name = "thread", DisplayName = "使用线程而非进程", Type = FioOptionType.Bool, DefaultValue = "0",
            Help = "Windows 上 fio 缺少进程共享互斥量，会强制使用线程；显式设 1 可消除告警并保证 JSON 输出干净。",
            Category = CatRuntime, Source = "options.c:4447",
        },

        // ---------------- 日志与图表数据 ----------------
        new()
        {
            Name = "write_bw_log", DisplayName = "输出带宽日志", Type = FioOptionType.String,
            Help = "生成 <名字>_bw.<任务号>.log：每行 `时间ms, 带宽KiB/s, 方向, 块大小, 优先级`，图表‘吞吐量’轴的数据源。",
            Category = CatLog, Source = "options.c:4470",
        },
        new()
        {
            Name = "write_iops_log", DisplayName = "输出 IOPS 日志", Type = FioOptionType.String,
            Help = "生成 <名字>_iops.<任务号>.log：每行 `时间ms, IOPS, 方向, 块大小, 优先级`，图表‘IOPS’轴的数据源。",
            Category = CatLog, Source = "options.c:4490",
        },
        new()
        {
            Name = "write_lat_log", DisplayName = "输出延迟日志", Type = FioOptionType.String,
            Help = "生成 _slat/_clat/_lat 日志，用于延迟分布曲线。",
            Category = CatLog, Source = "options.c:4480",
        },
        new()
        {
            Name = "log_avg_msec", DisplayName = "日志平均窗口", Type = FioOptionType.Integer, DefaultValue = "0", Min = 0, Unit = "ms",
            Help = "采样窗口（ms）。0 = 每个 IO 记一行（数据量巨大）；做曲线建议设 1000。",
            Category = CatLog, Source = "options.c:4512",
        },
        new()
        {
            Name = "bwavgtime", DisplayName = "带宽平均窗口", Type = FioOptionType.Integer, DefaultValue = "500", Min = 0, Unit = "ms",
            Help = "带宽计算窗口（ms）。实际生效值 = min(log_avg_msec, bwavgtime)，想看 1 秒粒度需把两者都设为 1000。",
            Category = CatLog, Source = "options.c:4692",
        },
        new()
        {
            Name = "iopsavgtime", DisplayName = "IOPS 平均窗口", Type = FioOptionType.Integer, DefaultValue = "500", Min = 0, Unit = "ms",
            Help = "IOPS 计算窗口（ms）。实际生效值 = min(log_avg_msec, iopsavgtime)。",
            Category = CatLog, Source = "options.c:4706",
        },
        new()
        {
            Name = "log_offset", DisplayName = "日志记录偏移量", Type = FioOptionType.Bool, DefaultValue = "0",
            Help = "日志每行追加 IO 偏移列，用于分析访问热区。",
            Category = CatLog, Source = "options.c:4588",
        },
        new()
        {
            Name = "per_job_logs", DisplayName = "按任务拆分日志", Type = FioOptionType.Bool, DefaultValue = "1",
            Help = "numjobs>1 时日志文件名是否带任务号。",
            Category = CatLog, Source = "options.c:4460",
        },
        new()
        {
            Name = "percentile_list", DisplayName = "延迟百分位列表", Type = FioOptionType.String, DefaultValue = "1:5:10:20:30:40:50:60:70:80:90:95:99:99.5:99.9:99.95:99.99",
            Help = "冒号分隔的百分位，决定结果里返回哪些延迟分位点。",
            Category = CatLog, Source = "options.c:4889",
        },

        // ---------------- 延迟统计 ----------------
        new()
        {
            Name = "latency_target", DisplayName = "延迟目标", Type = FioOptionType.SizeOrTime,
            Help = "目标延迟；设置后 fio 会尝试在满足该延迟的前提下调整队列深度。",
            Category = CatLatency, Source = "options.c:4057",
        },
        new()
        {
            Name = "latency_window", DisplayName = "延迟窗口", Type = FioOptionType.SizeOrTime,
            Help = "计算延迟达标率的滑动窗口时间。",
            Category = CatLatency, Source = "options.c:4067",
        },
        new()
        {
            Name = "latency_percentile", DisplayName = "延迟达标百分位", Type = FioOptionType.String, DefaultValue = "100",
            Help = "在该百分位内满足 latency_target 才算达标。",
            Category = CatLatency, Source = "options.c:4077",
        },
        new()
        {
            Name = "disable_lat", DisplayName = "关闭总延迟统计", Type = FioOptionType.Bool, DefaultValue = "0",
            Help = "关闭 lat（总延迟）统计以降低开销。",
            Category = CatLatency, Source = "options.c:4948",
        },
        new()
        {
            Name = "disable_clat", DisplayName = "关闭完成延迟统计", Type = FioOptionType.Bool, DefaultValue = "0",
            Help = "关闭 clat（完成延迟）统计。",
            Category = CatLatency, Source = "options.c:4960",
        },
        new()
        {
            Name = "disable_slat", DisplayName = "关闭提交延迟统计", Type = FioOptionType.Bool, DefaultValue = "0",
            Help = "关闭 slat（提交延迟）统计。",
            Category = CatLatency, Source = "options.c:4972",
        },

        // ---------------- 命令行（不进 job 文件） ----------------
        new()
        {
            Name = "readonly", DisplayName = "只读安全模式", Type = FioOptionType.Bool, DefaultValue = "0",
            Scope = FioOptionScope.CommandLine, CommandLineName = "readonly",
            Help = "fio 命令行开关 --readonly：任何写操作都会被拒绝。开发调试时强烈建议开启。",
            Category = CatCli, Source = "init.c:156",
        },
        new()
        {
            Name = "output-format", DisplayName = "输出格式", Type = FioOptionType.Enum, DefaultValue = "json",
            Scope = FioOptionScope.CommandLine, CommandLineName = "output-format",
            Choices = OutputFormatChoices,
            Help = "结果输出格式。采集结构化结果用 json。",
            Category = CatCli, Source = "init.c:121",
        },
        new()
        {
            Name = "output", DisplayName = "结果输出文件", Type = FioOptionType.String,
            Scope = FioOptionScope.CommandLine, CommandLineName = "output",
            Help = "把 fio 输出写到文件（JSON 前置脏文本也要按首个 '{' 截断）。",
            Category = CatCli, Source = "init.c:101",
        },
        new()
        {
            Name = "eta", DisplayName = "进度显示", Type = FioOptionType.Enum, DefaultValue = "always",
            Scope = FioOptionScope.CommandLine, CommandLineName = "eta", Choices = EtaChoices,
            Help = "是否输出实时进度行；做进度条/实时曲线时设为 always。",
            Category = CatCli, Source = "init.c:161",
        },
        new()
        {
            Name = "status-interval", DisplayName = "状态刷新间隔", Type = FioOptionType.SizeOrTime, Unit = "ms",
            Scope = FioOptionScope.CommandLine, CommandLineName = "status-interval",
            Help = "实时状态行输出间隔，如 1000ms。",
            Category = CatCli, Source = "init.c:269",
        },
        new()
        {
            Name = "aux-path", DisplayName = "辅助文件目录", Type = FioOptionType.String,
            Scope = FioOptionScope.CommandLine, CommandLineName = "aux-path",
            Help = "日志/状态等辅助文件的输出目录。",
            Category = CatCli, Source = "init.c:294",
        },
    ];

    private static readonly Dictionary<string, FioOptionDefinition> ByName =
        All.GroupBy(o => o.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>按参数名查找（忽略大小写）。</summary>
    public static FioOptionDefinition? Find(string name) =>
        ByName.TryGetValue(name, out var def) ? def : null;

    /// <summary>全部作业文件参数。</summary>
    public static IEnumerable<FioOptionDefinition> JobFileOptions =>
        All.Where(o => o.BelongsToJobFile);

    /// <summary>全部命令行参数。</summary>
    public static IEnumerable<FioOptionDefinition> CommandLineOptions =>
        All.Where(o => o.BelongsToCommandLine);

    /// <summary>按分组名聚合，界面据此分栏。</summary>
    public static IEnumerable<(string Category, IReadOnlyList<FioOptionDefinition> Options)> GroupedByCategory()
    {
        string[] order = [CatTarget, CatIo, CatRuntime, CatLog, CatLatency, CatCli];
        foreach (var cat in order)
        {
            var items = All.Where(o => o.Category == cat).ToList();
            if (items.Count > 0)
                yield return (cat, items);
        }
    }
}
