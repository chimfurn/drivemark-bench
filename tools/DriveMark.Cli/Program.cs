using System.Globalization;
using System.IO;
using DriveMark.Chart;
using DriveMark.Core;
using DriveMark.Core.Charts;
using DriveMark.Core.Execution;
using DriveMark.Core.Options;
using DriveMark.Core.Parsing;

namespace DriveMark.Cli;

/// <summary>
/// DriveMark 命令行工具：不启动界面，用于
/// 1) 批量渲染图表 PNG（视觉回归 / 自动化验证）；
/// 2) 生成与校验 fio job 文件（不执行 fio）；
/// 3) 解析既有的 fio 日志/JSON。
/// 任何命令都不会调用 fio，也不会写用户的磁盘（只写用户指定的输出文件）。
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            return args[0].ToLowerInvariant() switch
            {
                "list" => ListCommand(),
                "targets" => TargetsCommand(),
                "clean" => CleanCommand(Options(args)),
                "run" => RunCommand(Options(args)),
                "render" => RenderCommand(Options(args)),
                "render-all" => RenderAllCommand(Options(args)),
                "job" => JobCommand(Options(args)),
                "validate" => ValidateCommand(Options(args)),
                "logs" => LogsCommand(Options(args)),
                "json" => JsonCommand(Options(args)),
                "help" or "-h" or "--help" => PrintUsageAndOk(),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[错误] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int PrintUsageAndOk()
    {
        PrintUsage();
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"[错误] 未知命令：{command}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            DriveMark 命令行工具（run 命令会真实调用 fio 做测试）

            用法：
              DriveMark.Cli list
                  列出内置数据集与测试预设

              DriveMark.Cli targets
                  列出本机可测试的磁盘（本地盘/可移动盘）、RAW/物理磁盘（只读）与 fio 路径

              DriveMark.Cli clean [--target X:\]
                  清理测试数据目录 <目标盘>\DriveMark-test（默认清理所有可测试磁盘）

              DriveMark.Cli run [--preset <预设名>] [--target <盘符>] [--size 256m]
                             [--runtime 5s] [--ramp 1s] [--bs 4k] [--iodepth 16]
                             [--rw randread] [--sampling 10] [--writes]
                             [--allow-system-write] [--keep-data] [--timeout 秒]
                             [--raw-device "<\\.\PhysicalDriveN|\\.\Volume{GUID}>"] [--raw-writes]
                  真正调用 fio 执行一次测试（默认预设为只读的 4K 随机读）。
                  含写操作的预设必须显式加 --writes；系统盘写测试还需 --allow-system-write。
                  测试数据结束后自动删除。
                  --raw-device：对 RAW/未格式化磁盘做测试（直接读设备，不创建文件）。
                  整盘（\\.\PhysicalDriveN）需要管理员权限；RAW 默认只读。
                  --raw-writes：允许 RAW 写入（**会覆盖设备数据、无法清理/恢复**），仅对确认可清空的盘使用。

              DriveMark.Cli render --dataset <名称|目录> --x <time|volume> --y <iops|mibps|lat> --out <png>
                     [--style line|scatter]
                     [--xmin N --xmax N --xstep N] [--ymin N --ymax N --ystep N]
                     [--title 文本] [--width 1200] [--height 700]
                  渲染单张图表 PNG（折线图或散点图）

              DriveMark.Cli render-all --outdir <目录>
                  为每个数据集渲染 4 种轴组合 + 自定义轴示例 + 散点图示例

              DriveMark.Cli job --preset <预设名> [--out <file>]
                  生成 fio job 文件文本

              DriveMark.Cli validate --preset <预设名>
                  校验预设参数，退出码 1 表示存在错误

              DriveMark.Cli logs --dir <日志目录>
                  解析目录中的 fio bw/iops 日志并打印摘要

              DriveMark.Cli json --file <fio输出的json>
                  解析 fio JSON 结果并打印摘要

            横轴 --x：time=时间(s)，volume=累计传输量(MiB)
            纵轴 --y：iops=IOPS，mibps=吞吐量(MiB/s)，lat=延迟(µs/ms)
            """);
    }

    // ---------------------------------------------------------------- 命令

    private static int ListCommand()
    {
        Console.WriteLine("内置数据集：");
        foreach (var (key, data) in DemoDatasets())
        {
            Console.WriteLine($"  {key,-12} {data.Name} · {SeriesBuilder.Describe(data)}");
            Console.WriteLine($"               {data.Description}");
        }

        Console.WriteLine();
        Console.WriteLine("测试预设（fio 参数，仅生成 job 文件）：");
        foreach (var preset in FioPresets.All)
        {
            var risk = preset.WritesData ? "[会写盘]" : "[只读]";
            Console.WriteLine($"  {preset.Name,-22} {risk} {preset.Description}");
        }
        return 0;
    }

    private static int TargetsCommand()
    {
        Console.WriteLine("可测试的磁盘（已就绪的本地磁盘 / 可移动磁盘）：");
        var candidates = TestTargetGuard.EnumerateCandidates();
        if (candidates.Count == 0)
            Console.WriteLine("  （没有找到可用的磁盘）");

        foreach (var info in candidates)
        {
            var tags = new List<string>();
            if (info.IsSystemDrive)
                tags.Add("系统盘，禁止写入测试");
            if (info.IsRemovable)
                tags.Add("可移动磁盘");

            Console.WriteLine($"  {info.DriveLetter}:  {info.VolumeLabel}  ({info.FileSystem})  " +
                              $"可用 {Units.Bytes(info.FreeBytes)} / {Units.Bytes(info.TotalBytes)}" +
                              (tags.Count > 0 ? "  [" + string.Join("；", tags) + "]" : string.Empty));
        }

        var defaultTarget = TestTargetGuard.GetDefaultCandidate();
        if (defaultTarget is not null)
            Console.WriteLine($"\n默认目标：{defaultTarget.DriveLetter}:（{defaultTarget.VolumeLabel}）");

        // RAW / 未格式化磁盘：fio 直接读设备（只读），不创建测试文件。
        Console.WriteLine("\nRAW / 物理磁盘（只读测试）目标：");
        var rawDevices = RawDeviceProbe.Enumerate();
        if (rawDevices.Count == 0)
        {
            Console.WriteLine("  （没有发现 RAW 卷或物理磁盘）");
        }
        else
        {
            foreach (var raw in rawDevices)
                Console.WriteLine($"  {raw.DevicePath,-26}  {raw.Describe()}");
            if (!RawDeviceProbe.IsElevated)
                Console.WriteLine("  注意：整盘（PhysicalDriveN）只读测试需要管理员权限；RAW 卷视系统权限而定。");
        }

        var rawFio = FioExecutableLocator.Locate();
        Console.WriteLine($"\nfio 可执行文件：{rawFio ?? "未找到"}");
        return candidates.Count > 0 ? 0 : 1;
    }

    /// <summary>清理测试数据目录（默认清理所有可测试磁盘）。</summary>
    private static int CleanCommand(Dictionary<string, string> opt)
    {
        var targets = new List<string>();
        if (opt.TryGetValue("target", out var singleTarget))
        {
            targets.Add(singleTarget);
        }
        else
        {
            targets.AddRange(TestTargetGuard.EnumerateCandidates().Select(candidate => candidate.RootPath));
        }

        var failures = 0;
        foreach (var root in targets)
        {
            Console.WriteLine($"清理 {root} 下的测试数据目录 {TestDataJanitor.GetDataRoot(root)} …");
            var report = TestDataJanitor.CleanStale(root);
            Console.WriteLine("  " + report.Describe());
            foreach (var error in report.Errors)
            {
                Console.WriteLine("  " + error);
                failures++;
            }
        }

        return failures == 0 ? 0 : 1;
    }

    /// <summary>真实执行一次 fio 测试（目标盘可选择）。</summary>
    private static int RunCommand(Dictionary<string, string> opt)
    {
        var presetName = opt.GetValueOrDefault("preset") ?? "图表采集·4K 随机读";
        var preset = FioPresets.Find(presetName);
        if (preset is null)
        {
            Console.Error.WriteLine($"[错误] 找不到预设：{presetName}");
            return 1;
        }

        var config = new FioJobConfig { JobName = "fio-gui-run" }.ApplyPreset(preset);
        if (opt.TryGetValue("size", out var size)) config.TrySet("size", size);
        if (opt.TryGetValue("runtime", out var runtime)) config.TrySet("runtime", runtime);
        if (opt.TryGetValue("ramp", out var ramp)) config.TrySet("ramp_time", ramp);
        if (opt.TryGetValue("bs", out var bs)) config.TrySet("bs", bs);
        if (opt.TryGetValue("iodepth", out var iodepth)) config.TrySet("iodepth", iodepth);
        if (opt.TryGetValue("rw", out var rw)) config.TrySet("rw", rw);
        if (opt.TryGetValue("jobs", out var jobs)) config.TrySet("numjobs", jobs);
        if (opt.TryGetValue("sampling", out var sampling) && int.TryParse(sampling, out var samplingMs))
            SimpleTestProfiles.ApplySampling(config, samplingMs);

        var rwEffective = config.EffectiveValue("rw") ?? "read";
        var isWrite = FioValueParser.IsWriteMode(rwEffective);
        var allowWrites = opt.ContainsKey("writes");
        var targetPath = opt.GetValueOrDefault("target") ?? DefaultTargetPath();
        var rawDevice = opt.GetValueOrDefault("raw-device");
        var isRaw = !string.IsNullOrWhiteSpace(rawDevice);

        if (isRaw && isWrite)
        {
            Console.Error.WriteLine("[拒绝] RAW 设备只允许只读测试：直接写 RAW 盘会覆盖盘上的全部数据。");
            return 2;
        }

        if (isRaw && !RawDeviceProbe.IsRawDevicePath(rawDevice))
        {
            Console.Error.WriteLine($"[错误] --raw-device 需要形如 \\\\.\\PhysicalDrive1 或 \\\\.\\Volume{{GUID}} 的设备路径，收到：{rawDevice}");
            return 2;
        }

        Console.WriteLine("=".PadRight(72, '='));
        Console.WriteLine($"测试预设：{preset.Name}");
        Console.WriteLine($"读写模式：{rwEffective}（{(isWrite ? "会写盘" : "不写盘")}）");
        Console.WriteLine($"目标盘  ：{(isRaw ? "RAW 只读测试 → " + rawDevice : targetPath)}");
        if (isRaw)
            Console.WriteLine(isWrite && opt.ContainsKey("raw-writes")
                ? "测试模式：RAW 写入（会覆盖设备数据、无法清理；整盘需要管理员权限）"
                : "测试模式：RAW 只读（不创建测试文件、不写盘；整盘需要管理员权限）");
        Console.WriteLine($"参数    ：size={config.EffectiveValue("size")} runtime={config.EffectiveValue("runtime")} " +
                          $"bs={config.EffectiveValue("bs")} iodepth={config.EffectiveValue("iodepth")} " +
                          $"采样={config.EffectiveValue("log_avg_msec")}ms");
        Console.WriteLine("=".PadRight(72, '='));
        Console.WriteLine();

        if (isWrite && !allowWrites)
        {
            Console.Error.WriteLine("[拒绝] 该预设包含写操作。确认目标是测试盘后，请显式加 --writes 参数。");
            return 2;
        }

        var request = new FioRunRequest
        {
            Config = config,
            TargetRoot = targetPath,
            AllowWrites = allowWrites,
            AllowSystemDriveWrite = opt.ContainsKey("allow-system-write"),
            DeleteTestDataAfterRun = !opt.ContainsKey("keep-data"),
            RawDevicePath = isRaw ? rawDevice : null,
            AllowRawWrites = isRaw && opt.ContainsKey("raw-writes"),
            HardTimeout = opt.TryGetValue("timeout", out var timeoutText) && int.TryParse(timeoutText, out var timeoutSec)
                ? TimeSpan.FromSeconds(timeoutSec)
                : null,
        };

        var runner = new FioRunner();
        runner.ProgressChanged += (_, progress) =>
        {
            var sample = progress.Sample;
            var read = sample.ReadMebibytesPerSecond is { } rmp
                ? $"r={rmp:F0}MiB/s"
                : string.Empty;
            var readIops = sample.ReadIops is { } ri ? $"r={ri / 1000:F1}k IOPS" : string.Empty;
            var write = sample.WriteMebibytesPerSecond is { } wmp ? $"w={wmp:F0}MiB/s" : string.Empty;
            var writeIops = sample.WriteIops is { } wi ? $"w={wi / 1000:F1}k IOPS" : string.Empty;
            var eta = sample.Eta is { } e ? $"eta {e.Minutes:00}m:{e.Seconds:00}s" : string.Empty;
            Console.WriteLine($"  [{progress.Elapsed.TotalSeconds,5:F1}s] {sample.Percent,5:F1}%  " +
                              $"{read} {readIops} {write} {writeIops}  {eta}");
        };

        var result = runner.RunAsync(request).GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine("-".PadRight(72, '-'));
        Console.WriteLine($"退出码：{result.ExitCode}   耗时：{result.Duration.TotalSeconds:F1} 秒   " +
                          $"状态：{(result.Succeeded ? "成功" : "失败")}");
        if (result.ErrorMessage is not null)
            Console.WriteLine($"错误：{result.ErrorMessage}");

        Console.WriteLine();
        Console.WriteLine("性能汇总：");
        foreach (var line in result.BuildMetricLines())
            Console.WriteLine("  " + line);

        Console.WriteLine();
        if (!string.IsNullOrWhiteSpace(result.RunDirectory))
        {
            Console.WriteLine($"运行目录：{result.RunDirectory}");
            if (result.JanitorNote is not null)
                Console.WriteLine($"清理提示：{result.JanitorNote}");
            if (!string.IsNullOrWhiteSpace(result.JobFilePath))
                Console.WriteLine($"job 文件：{result.JobFilePath}");
            foreach (var log in result.LogFiles)
                Console.WriteLine($"日志文件：{log}");
        }

        Console.WriteLine();
        Console.WriteLine($"测试数据：{Units.Bytes(result.TestDataBytes)}（{result.TestDataFiles.Count} 个文件），" +
                          $"清理{(result.CleanupSucceeded ? "成功" : "失败")}");
        Console.WriteLine($"图表数据集：{SeriesBuilder.Describe(result.DataSet)}");
        if (!result.Succeeded)
            return 1;

        Console.WriteLine();
        Console.WriteLine("提示：可用下面的命令把本次日志画成图：");
        Console.WriteLine($"  DriveMark.Cli render --dataset \"{result.RunDirectory}\" --x time --y mibps --out run.png");
        Console.WriteLine($"  DriveMark.Cli render --dataset \"{result.RunDirectory}\" --x volume --y iops --out run-volume.png");

        return result.Succeeded && result.CleanupSucceeded ? 0 : 1;
    }

    private static int RenderCommand(Dictionary<string, string> opt)
    {
        var data = ResolveDataset(opt.GetValueOrDefault("dataset", "ssd-mixed"));
        var x = ParseX(opt.GetValueOrDefault("x", "time"));
        var y = ParseY(opt.GetValueOrDefault("y", "mibps"));
        var width = ParseInt(opt.GetValueOrDefault("width"), 1200);
        var height = ParseInt(opt.GetValueOrDefault("height"), 700);
        var style = ParseRenderStyle(opt.GetValueOrDefault("style"));

        var model = BuildModel(data, x, y, opt, opt.GetValueOrDefault("title"), style);

        var outPath = opt.GetValueOrDefault("out") ?? $"chart-{x}-{y}.png";
        ChartImageExporter.SavePng(model, outPath, width, height);

        PrintModelSummary(model, data);
        Console.WriteLine($"已输出：{Path.GetFullPath(outPath)}");
        return 0;
    }

    private static int RenderAllCommand(Dictionary<string, string> opt)
    {
        var outDir = opt.GetValueOrDefault("outdir") ?? "charts";
        Directory.CreateDirectory(outDir);
        var written = new List<string>();

        foreach (var (key, data) in DemoDatasets())
        {
            foreach (var x in new[] { XAxisDimension.ElapsedSeconds, XAxisDimension.TransferredMebibytes })
            {
                foreach (var y in new[] { YAxisMetric.Iops, YAxisMetric.ThroughputMebibytesPerSecond })
                {
                    var model = SeriesBuilder.Build(data, x, y);
                    var file = Path.Combine(outDir, $"{key}-x{ShortX(x)}-y{ShortY(y)}.png");
                    ChartImageExporter.SavePng(model, file, 1200, 700);
                    written.Add(file);
                    Console.WriteLine($"  {Path.GetFileName(file),-34} {DescribeAxes(model)}");
                }
            }

            // 自定义坐标轴示例：显式指定范围与步长（验证"自定义坐标轴"能力）
            var custom = SeriesBuilder.Build(
                data,
                XAxisDimension.ElapsedSeconds,
                YAxisMetric.ThroughputMebibytesPerSecond,
                xAxis: new AxisSpec { AutoScale = false, ManualMin = 10, ManualMax = 50, ManualStep = 5 },
                yAxis: new AxisSpec { AutoScale = false, ManualMin = 0, ManualMax = 500, ManualStep = 100 },
                title: $"{data.Name} · 自定义坐标轴");
            var customFile = Path.Combine(outDir, $"{key}-custom-axes.png");
            ChartImageExporter.SavePng(custom, customFile, 1200, 700);
            written.Add(customFile);
            Console.WriteLine($"  {Path.GetFileName(customFile),-34} {DescribeAxes(custom)}");

            // 散点图示例：同样的数据、只画离散点
            var scatter = SeriesBuilder.Build(
                data,
                XAxisDimension.ElapsedSeconds,
                YAxisMetric.ThroughputMebibytesPerSecond,
                renderStyle: ChartRenderStyle.Scatter,
                title: $"{data.Name} · 散点图");
            var scatterFile = Path.Combine(outDir, $"{key}-scatter.png");
            ChartImageExporter.SavePng(scatter, scatterFile, 1200, 700);
            written.Add(scatterFile);
            Console.WriteLine($"  {Path.GetFileName(scatterFile),-34} {DescribeAxes(scatter)}");
        }

        Console.WriteLine();
        Console.WriteLine($"共输出 {written.Count} 张 PNG 到 {Path.GetFullPath(outDir)}");
        return 0;
    }

    private static int JobCommand(Dictionary<string, string> opt)
    {
        var presetName = opt.GetValueOrDefault("preset");
        FioJobConfig config;
        if (presetName is null)
        {
            config = FioPresets.CreateDefault();
        }
        else
        {
            var preset = FioPresets.Find(presetName) ??
                         throw new ArgumentException($"找不到预设：{presetName}（可用 --list 查看）");
            config = new FioJobConfig { JobName = "fio-gui-job" }.ApplyPreset(preset);
        }

        var text = config.BuildJobFile("由 DriveMark 生成；本工具不会自动执行 fio。");
        var outPath = opt.GetValueOrDefault("out");
        if (outPath is null)
        {
            Console.Write(text);
        }
        else
        {
            File.WriteAllText(outPath, text);
            Console.WriteLine($"已写入 job 文件：{Path.GetFullPath(outPath)}");
        }

        var validation = config.Validate();
        Console.WriteLine();
        Console.WriteLine($"校验：{validation.Issues.Count} 条（错误 {validation.Errors.Count()} / 警告 {validation.Warnings.Count()}）");
        foreach (var issue in validation.Issues)
            Console.WriteLine("  " + issue);

        Console.WriteLine();
        Console.WriteLine("等价命令行：");
        Console.WriteLine("  fio " + string.Join(" ", config.BuildCommandLineArguments("job.fio")));
        return validation.HasErrors ? 1 : 0;
    }

    private static int ValidateCommand(Dictionary<string, string> opt)
    {
        var presetName = opt.GetValueOrDefault("preset") ?? FioPresets.All[0].Name;
        var preset = FioPresets.Find(presetName) ??
                     throw new ArgumentException($"找不到预设：{presetName}");

        var config = new FioJobConfig().ApplyPreset(preset);
        var result = config.Validate();

        Console.WriteLine($"预设：{preset.Name}（{(preset.WritesData ? "会写盘" : "只读")}）");
        Console.WriteLine($"参数 {config.ModifiedCount} 项：");
        foreach (var (k, v) in config.Values)
            Console.WriteLine($"  {k}={v}");

        Console.WriteLine();
        Console.WriteLine($"校验结果：{result.Issues.Count} 条");
        foreach (var issue in result.Issues)
            Console.WriteLine("  " + issue);

        Console.WriteLine();
        Console.WriteLine("生成的 job 文件：");
        Console.WriteLine(config.BuildJobFile("校验用"));
        return result.HasErrors ? 1 : 0;
    }

    private static int LogsCommand(Dictionary<string, string> opt)
    {
        var dir = opt.GetValueOrDefault("dir") ?? WorkspaceLocator.DemoLogsDirectory();
        var data = FioLogDataSetLoader.FromDirectory(dir);
        Console.WriteLine($"目录：{dir}");
        Console.WriteLine(SeriesBuilder.Describe(data));
        PrintDirectionSummary("带宽日志 KiB/s", data.BandwidthSamples);
        PrintDirectionSummary("IOPS 日志", data.IopsSamples);
        return 0;
    }

    private static int JsonCommand(Dictionary<string, string> opt)
    {
        var file = opt.GetValueOrDefault("file") ??
                   throw new ArgumentException("缺少 --file");
        var result = FioJsonParser.ParseFile(file);
        Console.WriteLine($"fio 版本：{result.FioVersion}  时间：{result.TimestampText}");
        foreach (var job in result.Jobs)
        {
            Console.WriteLine($"job: {job.JobName}  error={job.Error}");
            if (job.Read is { } r)
                Console.WriteLine($"  读: {Units.Iops(r.Iops)} IOPS, {Units.MebibytesPerSecond(r.MebibytesPerSecond)}, " +
                                  $"平均延迟 {r.LatencyMeanMicroseconds:F1} µs, P99 {r.ClatP99Ns / 1000:F1} µs");
            if (job.Write is { } w)
                Console.WriteLine($"  写: {Units.Iops(w.Iops)} IOPS, {Units.MebibytesPerSecond(w.MebibytesPerSecond)}, " +
                                  $"平均延迟 {w.LatencyMeanMicroseconds:F1} µs, P99 {w.ClatP99Ns / 1000:F1} µs");
        }
        return 0;
    }

    // ---------------------------------------------------------------- 辅助

    private static string DefaultTargetPath()
    {
        return TestTargetGuard.GetDefaultCandidate()?.RootPath
               ?? TestTargetGuard.SystemDriveRoot;
    }

    private static ChartModel BuildModel(
        ChartDataSet data, XAxisDimension x, YAxisMetric y, Dictionary<string, string> opt, string? title,
        ChartRenderStyle renderStyle = ChartRenderStyle.Line)
    {
        var xAxis = BuildAxisSpec(opt, "x");
        var yAxis = BuildAxisSpec(opt, "y");
        return SeriesBuilder.Build(data, x, y, xAxis, yAxis, title, renderStyle: renderStyle);
    }

    private static ChartRenderStyle ParseRenderStyle(string? value) => value?.ToLowerInvariant() switch
    {
        "scatter" or "dot" or "points" => ChartRenderStyle.Scatter,
        _ => ChartRenderStyle.Line,
    };

    private static AxisSpec? BuildAxisSpec(Dictionary<string, string> opt, string prefix)
    {
        var min = opt.GetValueOrDefault($"{prefix}min");
        var max = opt.GetValueOrDefault($"{prefix}max");
        var step = opt.GetValueOrDefault($"{prefix}step");
        if (min is null && max is null && step is null)
            return null;

        return new AxisSpec
        {
            AutoScale = false,
            ManualMin = min is null ? null : ParseDouble(min),
            ManualMax = max is null ? null : ParseDouble(max),
            ManualStep = step is null ? null : ParseDouble(step),
        };
    }

    private static void PrintModelSummary(ChartModel model, ChartDataSet data)
    {
        Console.WriteLine($"数据集：{data.Name} · {SeriesBuilder.Describe(data)}");
        Console.WriteLine(DescribeAxes(model));
        foreach (var series in model.Series)
            Console.WriteLine($"  曲线 {series.Name}: {series.Count} 点, 平均 {model.FormatY(series.MeanY)}, 峰值 {model.FormatY(series.MaxY)}");
    }

    private static string DescribeAxes(ChartModel model)
    {
        var x = model.ResolveX();
        var y = model.ResolveY();
        return $"X[{model.XTitle}] {model.FormatX(x.Min)}~{model.FormatX(x.Max)} 步长 {model.FormatX(x.Step)}" +
               $" | Y[{model.YTitle}] {model.FormatY(y.Min)}~{model.FormatY(y.Max)} 步长 {model.FormatY(y.Step)}" +
               $" | {model.Series.Count} 条曲线";
    }

    private static void PrintDirectionSummary(string label, IReadOnlyList<FioLogSample> samples)
    {
        if (samples.Count == 0)
        {
            Console.WriteLine($"{label}: 无数据");
            return;
        }

        foreach (var group in samples.GroupBy(s => s.Direction).OrderBy(g => g.Key))
        {
            var name = group.Key switch { 0 => "读", 1 => "写", 2 => "trim", 3 => "sync", _ => $"ddir={group.Key}" };
            var integrator = VolumeIntegrator.FromBandwidth(group);
            Console.WriteLine($"{label} [{name}]: {group.Count()} 点, " +
                              $"均值 {group.Average(s => s.Value):F1}, 峰值 {group.Max(s => s.Value):F1}, " +
                              $"累计传输 {Units.Mebibytes(integrator.TotalMib)}");
        }
    }

    private static IEnumerable<(string Key, ChartDataSet Data)> DemoDatasets()
    {
        var demoDir = WorkspaceLocator.DemoLogsDirectory();
        var ssd = FioLogDataSetLoader.FromFiles(
            "SSD 混合读写（4K）",
            "合成示例：4K 随机读写混合，含 5s 预热与 34-37s 掉速。非实测数据。",
            Path.Combine(demoDir, "ssd-mixed_bw.1.log"),
            Path.Combine(demoDir, "ssd-mixed_iops.1.log"),
            isSynthetic: true);

        var hdd = FioLogDataSetLoader.FromFiles(
            "HDD 顺序写（1M）",
            "合成示例：1M 顺序写，含盘区差异造成的慢漂移。非实测数据。",
            Path.Combine(demoDir, "hdd-write_bw.1.log"),
            Path.Combine(demoDir, "hdd-write_iops.1.log"),
            isSynthetic: true);

        yield return ("ssd-mixed", ssd);
        yield return ("hdd-write", hdd);
    }

    private static ChartDataSet ResolveDataset(string spec)
    {
        foreach (var (key, data) in DemoDatasets())
        {
            if (string.Equals(key, spec, StringComparison.OrdinalIgnoreCase))
                return data;
        }

        if (Directory.Exists(spec))
            return FioLogDataSetLoader.FromDirectory(spec);

        throw new ArgumentException($"找不到数据集：{spec}（可用 ssd-mixed / hdd-write，或传入包含 fio 日志的目录）");
    }

    private static Dictionary<string, string> Options(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                continue;

            var key = arg[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result[key] = args[++i];
            }
            else
            {
                result[key] = "1";
            }
        }
        return result;
    }

    private static XAxisDimension ParseX(string value) => value.ToLowerInvariant() switch
    {
        "time" or "seconds" or "t" => XAxisDimension.ElapsedSeconds,
        "volume" or "bytes" or "mib" or "v" => XAxisDimension.TransferredMebibytes,
        _ => throw new ArgumentException($"--x 只能是 time 或 volume，收到 {value}"),
    };

    private static YAxisMetric ParseY(string value) => value.ToLowerInvariant() switch
    {
        "iops" or "i" => YAxisMetric.Iops,
        "mibps" or "mibs" or "throughput" or "bw" or "m" => YAxisMetric.ThroughputMebibytesPerSecond,
        "lat" or "latency" or "延迟" => YAxisMetric.Latency,
        _ => throw new ArgumentException($"--y 只能是 iops / mibps / lat，收到 {value}"),
    };

    private static int ParseInt(string? s, int fallback) =>
        int.TryParse(s, out var v) ? v : fallback;

    private static double ParseDouble(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new ArgumentException($"不是合法数字：{s}");

    private static string ShortX(XAxisDimension x) => x == XAxisDimension.ElapsedSeconds ? "time" : "volume";

    private static string ShortY(YAxisMetric y) =>
        y == YAxisMetric.Iops ? "iops" : "mibps";
}
