using DriveMark.Core.Charts;
using DriveMark.Core.Options;
using DriveMark.Core.Parsing;

namespace DriveMark.Core.Execution;

/// <summary>一次 fio 测试的请求参数。</summary>
public sealed class FioRunRequest
{
    /// <summary>fio 作业配置（会被克隆，不会被修改）。</summary>
    public required FioJobConfig Config { get; init; }

    /// <summary>测试目标盘（由调用方指定；默认取系统盘根目录）。</summary>
    public string TargetRoot { get; init; } = TestTargetGuard.SystemDriveRoot;

    /// <summary>测试数据文件名（位于 &lt;目标盘&gt;:\DriveMark-test\&lt;运行号&gt;\ 下）。</summary>
    public string TestFileName { get; init; } = "fio-test.dat";

    /// <summary>是否允许写操作。配置里 rw 含写时必须置 true，否则拒绝运行。</summary>
    public bool AllowWrites { get; init; }

    /// <summary>是否启用 fio 日志平滑（最小 100ms 窗口；默认关闭）。</summary>
    public bool EnableFioLogSmoothing { get; init; }

    /// <summary>
    /// 是否允许在系统盘上做写测试。默认 false（系统盘禁止写入测试），
    /// 普通用户界面不暴露此开关。
    /// </summary>
    public bool AllowSystemDriveWrite { get; init; }

    /// <summary>运行结束后是否删除测试数据（默认删除，绝不残留占用空间）。</summary>
    public bool DeleteTestDataAfterRun { get; init; } = true;

    /// <summary>fio.exe 路径；null 表示自动查找。</summary>
    public string? FioExecutablePath { get; init; }

    /// <summary>硬超时；null 表示按 runtime/ramp_time 自动推算。</summary>
    public TimeSpan? HardTimeout { get; init; }

    /// <summary>运行目录的父目录（放 job 文件、JSON、日志）；默认工作区 runtime\runs。</summary>
    public string? RunRootDirectory { get; init; }

    /// <summary>允许测试的盘符根；默认仅 E 盘。</summary>
    public IReadOnlyList<string>? AllowedTargetRoots { get; init; }

    /// <summary>fio 状态刷新间隔（毫秒），用于实时进度。</summary>
    public int StatusIntervalMs { get; init; } = 1000;

    /// <summary>
    /// 原始设备路径（如 <c>\\.\PhysicalDrive1</c> 或 <c>\\.\Volume{GUID}</c>）。
    /// 设置后进入 RAW 只读模式：不创建测试文件、不清空目标设备，并强制 fio 只读。
    /// </summary>
    public string? RawDevicePath { get; init; }

    /// <summary>
    /// RAW 设备是否允许写入。默认 false（只读）。
    /// 注意：RAW 写入会直接覆盖设备上的数据，且**无法清理/恢复**；只有用户显式确认后才应为 true。
    /// </summary>
    public bool AllowRawWrites { get; init; }
}

/// <summary>一条实时进度（来自 fio 的 stdout 进度行）。</summary>
public sealed record FioRunProgress(ProgressSample Sample, TimeSpan Elapsed, string RawLine);

/// <summary>一批实时日志采样（来自 fio 正在写的日志文件）。</summary>
public sealed class FioLogSamplesEventArgs(IReadOnlyList<FioLogTailSample> samples) : EventArgs
{
    public IReadOnlyList<FioLogTailSample> Samples { get; } = samples;
}

/// <summary>测试运行结果。</summary>
public sealed class FioRunResult
{
    public required string RunDirectory { get; init; }
    public required string CommandLine { get; init; }
    public required string TestTargetDescription { get; init; }
    public int ExitCode { get; init; }
    public bool Cancelled { get; init; }
    public bool TimedOut { get; init; }
    public TimeSpan Duration { get; init; }

    /// <summary>fio 的 JSON 结果（失败时为 null）。</summary>
    public FioJsonResult? Json { get; init; }

    /// <summary>从本次运行日志解析出的图表数据集（真实数据）。</summary>
    public ChartDataSet DataSet { get; init; } = ChartDataSet.Empty("空", "无数据");

    /// <summary>错误信息（退出码非 0、超时、清理失败等）。</summary>
    public string? ErrorMessage { get; init; }

    public IReadOnlyList<string> StderrTail { get; init; } = [];
    public IReadOnlyList<string> StdoutTail { get; init; } = [];
    public IReadOnlyList<string> LogFiles { get; init; } = [];
    public string? JobFilePath { get; init; }
    public string? JsonFilePath { get; init; }

    /// <summary>本次在测试盘上创建的数据文件。</summary>
    public IReadOnlyList<string> TestDataFiles { get; init; } = [];
    public long TestDataBytes { get; init; }

    /// <summary>测试数据是否已清理干净。</summary>
    public bool CleanupSucceeded { get; init; } = true;

    /// <summary>运行前清理上次残留数据的说明（没有残留则为 null）。</summary>
    public string? JanitorNote { get; init; }

    public bool Succeeded => ExitCode == 0 && !Cancelled && !TimedOut && ErrorMessage is null;

    /// <summary>汇总读/写性能指标，供界面与命令行显示。</summary>
    public IReadOnlyList<string> BuildMetricLines()
    {
        if (Json is null)
            return ["（没有解析到 fio JSON 结果）"];

        var lines = new List<string> { $"fio 版本：{Json.FioVersion}" };
        foreach (var job in Json.Jobs)
        {
            if (job.Error != 0)
                lines.Add($"job {job.JobName}: error={job.Error}");

            if (job.Read is { } read)
            {
                lines.Add($"读：{Units.Iops(read.Iops)} IOPS，{Units.MebibytesPerSecond(read.MebibytesPerSecond)}，" +
                          $"平均延迟 {read.LatencyMeanMicroseconds:F1} µs，P99 {read.ClatP99Ns / 1000d:F1} µs，" +
                          $"共 {Units.Bytes(read.IoBytes)}");
            }

            if (job.Write is { } write)
            {
                lines.Add($"写：{Units.Iops(write.Iops)} IOPS，{Units.MebibytesPerSecond(write.MebibytesPerSecond)}，" +
                          $"平均延迟 {write.LatencyMeanMicroseconds:F1} µs，P99 {write.ClatP99Ns / 1000d:F1} µs，" +
                          $"共 {Units.Bytes(write.IoBytes)}");
            }
        }

        return lines;
    }
}
