using System.Diagnostics;
using System.IO;
using System.Text;
using DriveMark.Core.Charts;
using DriveMark.Core.Options;
using DriveMark.Core.Parsing;

namespace DriveMark.Core.Execution;

/// <summary>
/// 真正的 fio 执行器：生成 job 文件、启动 fio 子进程、实时解析进度、收集 JSON 与日志，
/// 并在结束后清理测试数据。
///
/// 安全设计（按用户要求）：
/// - 只允许 E 盘（<see cref="TestTargetGuard"/>）；
/// - 写测试必须显式允许；
/// - 测试数据只写入 <c>目标盘:\DriveMark-test\&lt;运行号&gt;\</c>，结束后整目录删除；
/// - 绝不对工作区以外的文件做删除，也不依赖 fio 的 unlink。
/// </summary>
public sealed class FioRunner
{
    /// <summary>实时进度事件（每收到一条 fio 进度行触发一次）。</summary>
    public event EventHandler<FioRunProgress>? ProgressChanged;

    /// <summary>
    /// 实时日志采样（来自 fio 正在写的 bw/iops/lat 日志，全分辨率）。
    /// 进度行最快 250ms 一条，而日志行按采样窗口输出（10ms 采样≈每秒 100 条），
    /// 实时绘图应使用本事件的数据。
    /// </summary>
    public event EventHandler<FioLogSamplesEventArgs>? LogSamplesReceived;

    private void EmitLogSamples(FioLogTailer tailer)
    {
        var samples = tailer.Poll();
        if (samples.Count > 0)
            LogSamplesReceived?.Invoke(this, new FioLogSamplesEventArgs(samples));
    }

    /// <summary>启动后台日志 tail（每 200ms 增量读取一次），返回停止函数。</summary>
    private (CancellationTokenSource Cts, Task Task) StartLogPump(FioLogTailer tailer,
        CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var task = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    EmitLogSamples(tailer);
                    await Task.Delay(200, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常结束。
            }
        });
        return (cts, task);
    }

    private static void StopLogPump(CancellationTokenSource cts, Task task)
    {
        try
        {
            cts.Cancel();
            task.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 忽略。
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <summary>fio 的每一行标准输出（可用于界面日志）。</summary>
    public event EventHandler<string>? OutputReceived;

    /// <summary>
    /// 构造 fio 命令行参数（与业务逻辑解耦，便于单元测试）。
    /// 关键点：进度走 stdout（fio 用 printf），结果走 --output 文件，因此两者不会互相污染。
    /// </summary>
    public static IReadOnlyList<string> BuildFioArguments(
        FioJobConfig config, string jobFilePath, string jsonOutputPath, int statusIntervalMs)
    {
        // 去掉与固定输出参数冲突的命令行级选项，避免重复传参产生歧义。
        var sanitized = config.Clone();
        foreach (var name in new[] { "output", "output-format", "aux-path", "eta", "status-interval" })
            sanitized.Reset(name);

        var args = new List<string>();
        args.AddRange(sanitized.BuildCommandLineArguments()); // 保留 readonly 等安全开关
        args.Add("--eta=always");
        // 注意（fio 源码 parse.c get_mult_time + init.c:2697）：
        // --eta-interval 必须带时间单位后缀（如 250ms）；裸数字会被当成"秒"导致 eta 被静默关闭。
        // 另外实测：同时传 --status-interval 会让 eta 行消失，所以这里只用 --eta-interval。
        // 最小 250ms（DISK_UTIL_MSEC）。
        var etaIntervalMs = Math.Max(250, statusIntervalMs);
        args.Add($"--eta-interval={etaIntervalMs}ms");
        args.Add("--thread=1");
        args.Add("--output-format=json");
        args.Add($"--output={jsonOutputPath}");
        args.Add(jobFilePath);
        return args;
    }

    /// <summary>执行一次 fio 测试。</summary>
    public async Task<FioRunResult> RunAsync(FioRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // RAW 设备（物理磁盘 / 原始卷）：走独立的只读执行路径，不创建任何测试文件。
        if (!string.IsNullOrWhiteSpace(request.RawDevicePath))
            return await RunRawAsync(request, cancellationToken).ConfigureAwait(false);

        var config = request.Config.Clone();

        // ---------- 1. 安全校验：目标盘、写权限、空间 ----------
        var target = TestTargetGuard.Inspect(request.TargetRoot, request.AllowedTargetRoots);
        var rw = config.EffectiveValue("rw") ?? "read";
        var isWrite = FioValueParser.IsWriteMode(rw);
        if (isWrite)
            TestTargetGuard.EnsureWritePermission(target, request.AllowWrites, request.AllowSystemDriveWrite);

        // 先清掉上次异常退出可能残留的测试数据（只动 目标盘:\DriveMark-test），再重新读取可用空间。
        var janitorReport = TestDataJanitor.CleanStale(target.RootPath);
        target = TestTargetGuard.Inspect(request.TargetRoot, request.AllowedTargetRoots);

        var sizeBytes = FioValueParser.ParseSizeBytes(config.EffectiveValue("size")) ?? 256L * 1024 * 1024;
        TestTargetGuard.EnsureCapacity(target, sizeBytes);

        var fioPath = FioExecutableLocator.Locate(request.FioExecutablePath)
                      ?? throw new FileNotFoundException("找不到 fio.exe（期望位于工作区 FIO\\fio.exe）");

        // ---------- 2. 运行目录 / 测试数据目录 ----------
        var runRoot = request.RunRootDirectory
                      ?? Path.Combine(WorkspaceLocator.FindRoot() ?? Directory.GetCurrentDirectory(), "runtime", "runs");
        Directory.CreateDirectory(runRoot);

        var runId = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4];
        var runDir = Path.Combine(runRoot, runId);
        Directory.CreateDirectory(runDir);

        var dataRoot = Path.Combine(target.RootPath, "DriveMark-test");
        var dataDir = Path.Combine(dataRoot, runId);
        EnsureSafeDataDirectory(dataDir, target.RootPath);
        Directory.CreateDirectory(dataDir);

        // ---------- 3. 注入运行期参数并写出 job 文件 ----------
        //
        // 重要坑位（实测）：fio 的 job 文件里 ':' 是**多文件分隔符**，
        // 因此 filename 绝不能写 "X:/xxx"（盘符冒号会被拆成两个文件名，产生名为 "X" 的垃圾文件）。
        // 正确做法：filename 用纯相对文件名，把 fio 的工作目录设为 E 盘数据目录；
        // 结果 JSON 与日志也用相对名，运行结束后再复制回 D 盘的运行目录。
        var testFileName = request.TestFileName;
        EnsureValidTestFileName(testFileName);

        config.TrySet("filename", testFileName);
        config.TrySet("thread", config.Get("thread") ?? "1");
        if (string.IsNullOrEmpty(config.Get("write_bw_log")))
            config.TrySet("write_bw_log", "fio-gui-bw");
        if (string.IsNullOrEmpty(config.Get("write_iops_log")))
            config.TrySet("write_iops_log", "fio-gui-iops");
        if (string.IsNullOrEmpty(config.Get("write_lat_log")))
            config.TrySet("write_lat_log", "fio-gui-lat");
        if (string.IsNullOrEmpty(config.Get("group_reporting")))
            config.TrySet("group_reporting", "1");
        if (string.IsNullOrEmpty(config.Get("invalidate")))
            config.TrySet("invalidate", "1");
        if (string.IsNullOrEmpty(config.Get("size")))
            config.TrySet("size", "256m");

        var jobFile = Path.Combine(runDir, "job.fio");
        File.WriteAllText(jobFile,
            config.BuildJobFile($"DriveMark 运行 {runId}；测试目标 {target.DriveLetter} 盘；本文件由程序自动生成。"));

        var jsonFileName = "result.json";
        var jsonFile = Path.Combine(runDir, jsonFileName);
        var args = BuildFioArguments(config, jobFile, jsonFileName, request.StatusIntervalMs);
        var commandLine = Quote(fioPath) + " " + string.Join(" ", args.Select(Quote));

        var stopwatch = Stopwatch.StartNew();
        var stdoutTail = new List<string>();
        var stderrTail = new List<string>();
        int exitCode = -1;
        var cancelled = false;
        var timedOut = false;
        string? runError = null;

        var dataFiles = new List<string>();
        long dataBytes = 0;
        var cleanupOk = true;
        FioJsonResult? json = null;
        IReadOnlyList<string> logFiles = [];
        ChartDataSet dataSet = ChartDataSet.Empty("空", "无数据");

        FioLogTailer? logTailer = null;
        CancellationTokenSource? logPumpCts = null;
        Task? logPumpTask = null;

        try
        {
            // ---------- 4. 启动 fio ----------
            // 实时绘图：tail fio 正在写的日志文件（全分辨率，10ms 采样≈每秒 100 条）。
            logTailer = new FioLogTailer(dataDir);
            (logPumpCts, logPumpTask) = StartLogPump(logTailer, cancellationToken);

            var psi = new ProcessStartInfo(fioPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // 工作目录设为 E 盘测试数据目录：job 文件里的相对 filename 与日志文件都会落在这里。
                WorkingDirectory = dataDir,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
            ApplyFioLogSmoothing(psi, request);
            StartRealtimePlugin(request.RealtimeLogPlugin, Path.Combine(dataDir, "live"), psi);

            var timeout = request.HardTimeout ?? ComputeTimeout(config);
            using var process = new Process { StartInfo = psi };
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            process.Start();

            var stdoutTask = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
                    {
                        lock (stdoutTail)
                        {
                            stdoutTail.Add(line);
                            if (stdoutTail.Count > 300)
                                stdoutTail.RemoveAt(0);
                        }

                        OutputReceived?.Invoke(this, line);

                        if (ProgressLineParser.TryParse(line, out var sample))
                            ProgressChanged?.Invoke(this, new FioRunProgress(sample, stopwatch.Elapsed, line));
                    }
                }
                catch
                {
                    // 进程被强制结束时流可能抛异常，忽略。
                }
            });

            var stderrTask = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                    {
                        lock (stderrTail)
                        {
                            stderrTail.Add(line);
                            if (stderrTail.Count > 300)
                                stderrTail.RemoveAt(0);
                        }
                    }
                }
                catch
                {
                    // 同上。
                }
            });

            try
            {
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    runError = "测试已被用户取消。";
                }
                else
                {
                    timedOut = true;
                    runError = $"运行超时（{timeout.TotalSeconds:F0} 秒），已强制终止 fio。";
                }

                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 忽略终止失败。
                }

                try
                {
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // 忽略。
                }
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            exitCode = process.HasExited ? process.ExitCode : -1;

            if (exitCode != 0 && runError is null)
            {
                var detail = stderrTail.Count > 0 ? "：" + stderrTail[^1] : string.Empty;
                runError = $"fio 退出码 {exitCode}{detail}";
            }

            // 停止实时日志 tail，并把最后一批日志行也发出去（保证实时曲线与日志一致）。
            if (logTailer is not null && logPumpCts is not null && logPumpTask is not null)
            {
                StopLogPump(logPumpCts, logPumpTask);
                logPumpCts = null;
                logPumpTask = null;
                EmitLogSamples(logTailer);
            }

            // ---------- 5. 把 E 盘数据目录里的结果 JSON 与日志复制回运行目录 ----------
            // （必须赶在 finally 的清理之前复制，否则日志会跟着测试数据一起被删掉。）
            CopyArtifactsToRunDirectory(dataDir, runDir, jsonFileName);

            // ---------- 6. 收集结果：JSON + 日志 ----------
            if (File.Exists(jsonFile))
            {
                try
                {
                    json = FioJsonParser.ParseFile(jsonFile);
                }
                catch (Exception ex)
                {
                    runError ??= $"fio JSON 解析失败：{ex.Message}";
                }
            }

            logFiles = Directory.Exists(runDir)
                ? Directory.GetFiles(runDir, "*.log").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : [];

            var bwLog = logFiles.FirstOrDefault(f => FioLogParser.KindFromFileName(f) == FioLogKind.Bandwidth);
            var iopsLog = logFiles.FirstOrDefault(f => FioLogParser.KindFromFileName(f) == FioLogKind.Iops);
            var latLog = logFiles.FirstOrDefault(file =>
            {
                var fileName = Path.GetFileName(file).ToLowerInvariant();
                return fileName.Contains("_lat.") && !fileName.Contains("_clat.") && !fileName.Contains("_slat.");
            });
            var bs = config.EffectiveValue("bs") ?? "?";
            var iodepth = config.EffectiveValue("iodepth") ?? "?";
            dataSet = FioLogDataSetLoader.FromFiles(
                name: $"{target.DriveLetter} 盘实测：{rw} {bs} QD{iodepth}",
                description: $"实测数据 · {runId} · {target.Describe()} · 运行 {stopwatch.Elapsed.TotalSeconds:F1}s",
                bandwidthLogPath: bwLog,
                iopsLogPath: iopsLog,
                isSynthetic: false,
                latencyLogPath: latLog);

            // 写一份人可读的运行摘要，方便在 results 目录里直接查看。
            WriteRunInfoFile(runDir, target.Describe(), rw, config, commandLine, json, dataSet,
                stopwatch.Elapsed, exitCode, runError);
        }
        finally
        {
            stopwatch.Stop();

            // 异常路径也要停掉实时日志 tail / 插件。
            if (logPumpCts is not null && logPumpTask is not null)
                StopLogPump(logPumpCts, logPumpTask);
            StopRealtimePlugin();

            // ---------- 6. 清理测试数据（只删本次创建的目录） ----------
            try
            {
                if (Directory.Exists(dataDir))
                {
                    foreach (var file in Directory.GetFiles(dataDir, "*", SearchOption.AllDirectories))
                    {
                        dataFiles.Add(file);
                        try
                        {
                            dataBytes += new FileInfo(file).Length;
                        }
                        catch
                        {
                            // 忽略单个文件统计失败。
                        }
                    }

                    if (request.DeleteTestDataAfterRun)
                    {
                        try
                        {
                            Directory.Delete(dataDir, recursive: true);
                        }
                        catch (Exception ex)
                        {
                            cleanupOk = false;
                            runError ??= $"测试数据清理失败：{ex.Message}（请手动检查 {dataDir}）";
                        }

                        if (Directory.Exists(dataDir))
                        {
                            cleanupOk = false;
                            runError ??= $"测试数据未能删除：{dataDir}";
                        }
                    }
                }

                // 父目录空了就一并删除，保持测试盘整洁。
                try
                {
                    if (Directory.Exists(dataRoot) && !Directory.EnumerateFileSystemEntries(dataRoot).Any())
                        Directory.Delete(dataRoot);
                }
                catch
                {
                    // 忽略。
                }
            }
            catch (Exception ex)
            {
                cleanupOk = false;
                runError ??= $"清理测试数据时出错：{ex.Message}";
            }
        }

        return new FioRunResult
        {
            RunDirectory = runDir,
            CommandLine = commandLine,
            TestTargetDescription = target.Describe(),
            ExitCode = exitCode,
            Cancelled = cancelled,
            TimedOut = timedOut,
            Duration = stopwatch.Elapsed,
            Json = json,
            DataSet = dataSet,
            ErrorMessage = runError,
            StderrTail = stderrTail.ToArray(),
            StdoutTail = stdoutTail.ToArray(),
            LogFiles = logFiles,
            JobFilePath = jobFile,
            JsonFilePath = jsonFile,
            TestDataFiles = dataFiles,
            TestDataBytes = dataBytes,
            CleanupSucceeded = cleanupOk,
            JanitorNote = janitorReport.HadWork || janitorReport.Errors.Count > 0
                ? janitorReport.Describe()
                : null,
        };
    }

    /// <summary>
    /// 校验测试数据文件名：必须是纯文件名。
    /// 原因（实测）：fio 的 job 文件里 ':' 与路径分隔符会破坏解析——
    /// "X:/x.dat" 会被当成两个文件名（"X" 和 "/x.dat"）。
    /// </summary>
    public static void EnsureValidTestFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.Contains(':') || fileName.Contains('/') || fileName.Contains('\\') ||
            fileName is "." or "..")
        {
            throw new InvalidOperationException(
                $"测试数据文件名必须是纯文件名（不能含盘符冒号或路径分隔符），当前为：{fileName ?? "(空)"}");
        }
    }

    /// <summary>
    /// RAW 设备（物理磁盘 / 原始卷）只读测试。
    ///
    /// 与普通文件测试完全隔离：
    /// - 不创建 DriveMark-test 目录、不写任何测试文件、结束后也不清理目标设备（只读不会留下数据）；
    /// - 强制 <c>rw=read</c> + <c>readonly=1</c>（fio 的写保护开关），写测试直接拒绝；
    /// - job/JSON/日志全部写在软件侧的运行目录（默认 runtime\runs 或 results）。
    /// </summary>
    private async Task<FioRunResult> RunRawAsync(FioRunRequest request, CancellationToken cancellationToken)
    {
        var config = request.Config.Clone();
        var rw = config.EffectiveValue("rw") ?? "read";
        var isWrite = FioValueParser.IsWriteMode(rw);
        var devicePath = request.RawDevicePath!.Trim();

        // ---------- 安全校验（校验失败时不创建任何目录/文件） ----------
        // 读测试永远只读；写 RAW 会永久覆盖设备数据且无法清理，只有用户显式允许后才放行（界面会二次确认）。
        if (isWrite && !request.AllowRawWrites)
        {
            return RawFailure(devicePath,
                "RAW 写入默认禁止：直接写 RAW 设备会覆盖盘上的全部数据且无法清理/恢复。" +
                "确认目标盘可以清空后，请勾选「允许 RAW 写入（覆盖数据）」并再次确认。");
        }

        if (!RawDeviceProbe.IsRawDevicePath(devicePath))
        {
            return RawFailure(devicePath,
                $"不是合法的原始设备路径：{devicePath}（应形如 \\\\.\\PhysicalDrive1 或 \\\\.\\Volume{{GUID}}）。");
        }

        if (!RawDeviceProbe.CanOpenRead(devicePath))
        {
            var hint = RawDeviceProbe.IsElevated
                ? "无法打开设备（可能被其它程序独占、设备已断开或路径不存在）。"
                : "整盘 RAW 测试需要管理员权限。请双击『启动DriveMark（管理员）.bat』或右键以管理员身份运行后再试。";
            return RawFailure(devicePath, "RAW 测试无法开始：" + hint);
        }

        var runRoot = request.RunRootDirectory
                      ?? Path.Combine(WorkspaceLocator.FindRoot() ?? Directory.GetCurrentDirectory(), "runtime", "runs");
        Directory.CreateDirectory(runRoot);
        var runId = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4];
        var runDir = Path.Combine(runRoot, runId);
        Directory.CreateDirectory(runDir);

        var info = RawDeviceProbe.Find(devicePath);
        var targetDescription = "RAW（" + (isWrite ? "写入" : "只读") + "）：" +
                                (info?.Describe() ?? devicePath);

        // ---------- 参数：直写/绕过缓存 + 单任务 ----------
        // 读测试：强制 rw=read + fio --readonly 双保险；
        // 写测试：保留用户选择的写模式（仅在显式允许时），数据直接覆盖设备、无法清理。
        if (isWrite)
        {
            config.TrySet("rw", rw);
            config.Reset("readonly");
        }
        else
        {
            config.TrySet("rw", "read");
            config.TrySet("readonly", "1"); // fio 自带写保护：任何写操作都会被 fio 拒绝
        }
        config.TrySet("direct", "1");
        config.TrySet("thread", "1");
        config.TrySet("filename", devicePath);
        if (string.IsNullOrEmpty(config.Get("bs")))
            config.TrySet("bs", "1m");
        if (string.IsNullOrEmpty(config.Get("size")))
            config.TrySet("size", "256m");
        if (string.IsNullOrEmpty(config.Get("write_bw_log")))
            config.TrySet("write_bw_log", "fio-gui-bw");
        if (string.IsNullOrEmpty(config.Get("write_iops_log")))
            config.TrySet("write_iops_log", "fio-gui-iops");
        if (string.IsNullOrEmpty(config.Get("write_lat_log")))
            config.TrySet("write_lat_log", "fio-gui-lat");
        if (string.IsNullOrEmpty(config.Get("group_reporting")))
            config.TrySet("group_reporting", "1");
        if (string.IsNullOrEmpty(config.Get("invalidate")))
            config.TrySet("invalidate", "1");
        if (string.IsNullOrEmpty(config.Get("time_based")) && !string.IsNullOrEmpty(config.Get("runtime")))
            config.TrySet("time_based", "1");

        var jobFile = Path.Combine(runDir, "job.fio");
        var jobHeader = isWrite
            ? $"DriveMark RAW 写入运行 {runId}；设备 {devicePath}；警告：测试数据直接覆盖设备、无法清理；本文件由程序自动生成。"
            : $"DriveMark RAW 只读运行 {runId}；设备 {devicePath}；本文件由程序自动生成。";
        File.WriteAllText(jobFile, config.BuildJobFile(jobHeader));

        var jsonFileName = "result.json";
        var jsonFile = Path.Combine(runDir, jsonFileName);
        var fioPath = FioExecutableLocator.Locate(request.FioExecutablePath)
                      ?? throw new FileNotFoundException("找不到 fio.exe（期望位于工作区 FIO\\fio.exe）");
        var args = BuildFioArguments(config, jobFile, jsonFileName, request.StatusIntervalMs);
        var commandLine = Quote(fioPath) + " " + string.Join(" ", args.Select(Quote));

        var stopwatch = Stopwatch.StartNew();
        var stdoutTail = new List<string>();
        var stderrTail = new List<string>();
        var exitCode = -1;
        var cancelled = false;
        var timedOut = false;
        string? runError = null;
        FioJsonResult? json = null;
        ChartDataSet dataSet = ChartDataSet.Empty("空", "无数据");

        var psi = new ProcessStartInfo(fioPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // RAW 模式不碰目标设备：job/JSON/日志都落在软件侧的运行目录。
            WorkingDirectory = runDir,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        ApplyFioLogSmoothing(psi, request);
        StartRealtimePlugin(request.RealtimeLogPlugin, Path.Combine(runDir, "live"), psi);

        var timeout = request.HardTimeout ?? ComputeTimeout(config);

        // 实时绘图：tail 运行目录里的日志（RAW 模式的日志直接写在软件侧）。
        var logTailer = new FioLogTailer(runDir);
        var (logPumpCts, logPumpTask) = StartLogPump(logTailer, cancellationToken);
        try
        {
        using (var process = new Process { StartInfo = psi })
        using (var timeoutCts = new CancellationTokenSource(timeout))
        using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
        {
            process.Start();

            var stdoutTask = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
                    {
                        lock (stdoutTail)
                        {
                            stdoutTail.Add(line);
                            if (stdoutTail.Count > 300)
                                stdoutTail.RemoveAt(0);
                        }

                        OutputReceived?.Invoke(this, line);

                        if (ProgressLineParser.TryParse(line, out var sample))
                            ProgressChanged?.Invoke(this, new FioRunProgress(sample, stopwatch.Elapsed, line));
                    }
                }
                catch
                {
                    // 进程被强制结束时流可能抛异常，忽略。
                }
            });

            var stderrTask = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                    {
                        lock (stderrTail)
                        {
                            stderrTail.Add(line);
                            if (stderrTail.Count > 300)
                                stderrTail.RemoveAt(0);
                        }
                    }
                }
                catch
                {
                    // 同上。
                }
            });

            try
            {
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    runError = "测试已被用户取消。";
                }
                else
                {
                    timedOut = true;
                    runError = $"运行超时（{timeout.TotalSeconds:F0} 秒），已强制终止 fio。";
                }

                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 忽略终止失败。
                }

                try
                {
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // 忽略。
                }
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            exitCode = process.HasExited ? process.ExitCode : -1;

            if (exitCode != 0 && runError is null)
            {
                var detail = stderrTail.Count > 0 ? "：" + stderrTail[^1] : string.Empty;
                runError = $"fio 退出码 {exitCode}{detail}";
            }
        }
        }
        finally
        {
            StopLogPump(logPumpCts, logPumpTask);
            EmitLogSamples(logTailer);
            StopRealtimePlugin();
        }

        if (File.Exists(jsonFile))
        {
            try
            {
                json = FioJsonParser.ParseFile(jsonFile);
            }
            catch (Exception ex)
            {
                runError ??= $"fio JSON 解析失败：{ex.Message}";
            }
        }

        var logFiles = Directory.Exists(runDir)
            ? Directory.GetFiles(runDir, "*.log").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : [];

        var bwLog = logFiles.FirstOrDefault(f => FioLogParser.KindFromFileName(f) == FioLogKind.Bandwidth);
        var iopsLog = logFiles.FirstOrDefault(f => FioLogParser.KindFromFileName(f) == FioLogKind.Iops);
        var latLog = logFiles.FirstOrDefault(file =>
        {
            var fileName = Path.GetFileName(file).ToLowerInvariant();
            return fileName.Contains("_lat.") && !fileName.Contains("_clat.") && !fileName.Contains("_slat.");
        });
        var bs = config.EffectiveValue("bs") ?? "?";
        var iodepth = config.EffectiveValue("iodepth") ?? "?";
        var modeLabel = isWrite ? "写入" : "只读";
        dataSet = FioLogDataSetLoader.FromFiles(
            name: $"RAW {modeLabel}实测：{bs} QD{iodepth}",
            description: $"RAW {modeLabel}实测 · {runId} · {devicePath} · 运行 {stopwatch.Elapsed.TotalSeconds:F1}s",
            bandwidthLogPath: bwLog,
            iopsLogPath: iopsLog,
            isSynthetic: false,
            latencyLogPath: latLog);

        // RAW 写入实际写到设备上的字节数（无法通过删文件回收）。
        long writtenBytes = 0;
        if (isWrite && json is not null)
            writtenBytes = json.Jobs.Sum(job => job.Write?.IoBytes ?? 0);

        stopwatch.Stop();
        var modeNote = isWrite
            ? "RAW 写入：测试数据直接覆盖设备、无法清理/恢复"
            : "RAW 只读：不创建测试文件、不清空目标设备";
        WriteRunInfoFile(runDir, targetDescription,
            $"{rw}（RAW {(isWrite ? "写入（数据无法清理）" : "强制只读")}）",
            config, commandLine, json, dataSet, stopwatch.Elapsed, exitCode, runError, modeNote);

        return new FioRunResult
        {
            RunDirectory = runDir,
            CommandLine = commandLine,
            TestTargetDescription = targetDescription,
            ExitCode = exitCode,
            Cancelled = cancelled,
            TimedOut = timedOut,
            Duration = stopwatch.Elapsed,
            Json = json,
            DataSet = dataSet,
            ErrorMessage = runError,
            StderrTail = stderrTail,
            StdoutTail = stdoutTail,
            LogFiles = logFiles,
            JobFilePath = jobFile,
            JsonFilePath = File.Exists(jsonFile) ? jsonFile : null,
            TestDataFiles = [],
            TestDataBytes = isWrite ? writtenBytes : 0,
            CleanupSucceeded = true,
            JanitorNote = isWrite
                ? "RAW 写入模式：测试数据直接覆盖设备，无法清理/恢复（该盘需要重新分区格式化才能正常使用）。"
                : "RAW 只读模式：不创建测试文件，也不清理目标设备。",
        };
    }

    private static FioRunResult RawFailure(string devicePath, string message) => new()
    {
        RunDirectory = string.Empty,
        CommandLine = string.Empty,
        TestTargetDescription = "RAW（只读）：" + devicePath,
        ExitCode = -1,
        ErrorMessage = message,
        CleanupSucceeded = true,
        JanitorNote = "RAW 只读模式未启动。",
    };

    /// <summary>按 runtime + ramp_time 推算超时（再加 120 秒余量用于文件布局）。</summary>
    private static TimeSpan ComputeTimeout(FioJobConfig config)
    {
        var runtime = FioValueParser.ParseDuration(config.EffectiveValue("runtime")) ?? TimeSpan.Zero;
        var ramp = FioValueParser.ParseDuration(config.EffectiveValue("ramp_time")) ?? TimeSpan.Zero;
        var total = runtime + ramp + TimeSpan.FromSeconds(120);
        return total < TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : total;
    }

    /// <summary>
    /// 把 fio 写在 E 盘数据目录里的结果文件与日志复制到 D 盘运行目录，
    /// 这样清理测试数据后分析数据仍然保留。
    /// </summary>
    private static void CopyArtifactsToRunDirectory(string sourceDirectory, string runDirectory, string jsonFileName)
    {
        if (!Directory.Exists(sourceDirectory))
            return;

        foreach (var file in Directory.GetFiles(sourceDirectory))
        {
            var name = Path.GetFileName(file);
            var isLog = name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
            var isJson = string.Equals(name, jsonFileName, StringComparison.OrdinalIgnoreCase);
            if (!isLog && !isJson)
                continue;

            try
            {
                File.Copy(file, Path.Combine(runDirectory, name), overwrite: true);
            }
            catch
            {
                // 单个文件复制失败不应影响整体结果。
            }
        }
    }

    /// <summary>
    /// 安全护栏：测试数据目录必须严格位于 <c>&lt;盘符根&gt;\DriveMark-test\&lt;运行号&gt;</c>，
    /// 目标也必须是盘符根目录。这样程序永远不可能写到/删到其它位置。
    /// </summary>
    private static void EnsureSafeDataDirectory(string dataDir, string targetRoot)
    {
        var fullTarget = Path.GetFullPath(targetRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var targetPathRoot = Path.GetPathRoot(fullTarget);
        if (targetPathRoot is null ||
            !string.Equals(fullTarget, targetPathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"拒绝运行：测试目标必须是盘符根目录，收到 {targetRoot}。");
        }

        var expectedRoot = Path.GetFullPath(Path.Combine(fullTarget, "DriveMark-test"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullData = Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!fullData.StartsWith(expectedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullData, expectedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"拒绝运行：测试数据目录不在安全位置（{dataDir}）。程序只会写入 {expectedRoot}。");
        }
    }

    /// <summary>在运行目录写一份人可读的 run-info.txt（结果目录里可直接查看）。</summary>
    private static void WriteRunInfoFile(
        string runDir,
        string targetDescription,
        string rw,
        FioJobConfig config,
        string commandLine,
        FioJsonResult? json,
        ChartDataSet dataSet,
        TimeSpan duration,
        int exitCode,
        string? error,
        string? modeNote = null)
    {
        try
        {
            var builder = new StringBuilder();
            builder.AppendLine("DriveMark 测试结果摘要");
            builder.AppendLine("==================");
            builder.AppendLine($"运行时间 : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"目标设备 : {targetDescription}");
            if (!string.IsNullOrWhiteSpace(modeNote))
                builder.AppendLine($"测试模式 : {modeNote}");
            builder.AppendLine($"结果状态 : {(exitCode == 0 && error is null ? "成功" : "失败")}" +
                               $"（退出码 {exitCode}，耗时 {duration.TotalSeconds:F1} 秒）");
            if (error is not null)
                builder.AppendLine($"错误信息 : {error}");

            builder.AppendLine($"读写模式 : rw={rw}  bs={config.EffectiveValue("bs")}  iodepth={config.EffectiveValue("iodepth")}");
            builder.AppendLine($"测试规模 : size={config.EffectiveValue("size")}  runtime={config.EffectiveValue("runtime")}  " +
                               $"ramp_time={config.EffectiveValue("ramp_time")}");
            builder.AppendLine($"采样间隔 : {config.EffectiveValue("log_avg_msec")} ms");
            builder.AppendLine($"数据统计 : {SeriesBuilder.Describe(dataSet)}");
            builder.AppendLine();
            builder.AppendLine("fio 命令 :");
            builder.AppendLine("  " + commandLine);

            if (json is not null)
            {
                builder.AppendLine();
                builder.AppendLine("性能汇总 :");
                foreach (var job in json.Jobs)
                {
                    if (job.Read is { } read)
                        builder.AppendLine($"  读: {Units.Iops(read.Iops)} IOPS, {Units.MebibytesPerSecond(read.MebibytesPerSecond)}, " +
                                           $"平均延迟 {read.LatencyMeanMicroseconds:F1} µs, P99 {read.ClatP99Ns / 1000d:F1} µs");
                    if (job.Write is { } write)
                        builder.AppendLine($"  写: {Units.Iops(write.Iops)} IOPS, {Units.MebibytesPerSecond(write.MebibytesPerSecond)}, " +
                                           $"平均延迟 {write.LatencyMeanMicroseconds:F1} µs, P99 {write.ClatP99Ns / 1000d:F1} µs");
                }
            }

            File.WriteAllText(Path.Combine(runDir, "run-info.txt"), builder.ToString());
        }
        catch
        {
            // 摘要写失败不影响测试结果。
        }
    }

    /// <summary>
    /// 把界面上的“fio 日志平滑”开关传给 fio：开启时设置 DRIVEMARK_FIO_MIN_LOG_MS=100，
    /// 关闭时不设置该环境变量，让 fio 保持上游原始行为。
    /// </summary>
    internal static void ApplyFioLogSmoothing(ProcessStartInfo psi, FioRunRequest request)
    {
        if (request.EnableFioLogSmoothing)
            psi.Environment["DRIVEMARK_FIO_MIN_LOG_MS"] = "100";
        else
            psi.Environment.Remove("DRIVEMARK_FIO_MIN_LOG_MS");
    }
    private IRealtimeLogPlugin? _activeRealtimePlugin;

    private void StartRealtimePlugin(IRealtimeLogPlugin? plugin, string liveDirectory, ProcessStartInfo psi)
    {
        if (plugin is null)
            return;

        try
        {
            Directory.CreateDirectory(liveDirectory);
            psi.Environment["DRIVEMARK_FIO_LIVE_DIR"] = liveDirectory;
            plugin.Start(liveDirectory, samples =>
            {
                if (samples.Count > 0)
                    LogSamplesReceived?.Invoke(this, new FioLogSamplesEventArgs(samples));
            });
            _activeRealtimePlugin = plugin;
        }
        catch
        {
            // 插件失败不能阻止测试；GUI 会退回进度采样。
            _activeRealtimePlugin = null;
        }
    }

    private void StopRealtimePlugin()
    {
        try { _activeRealtimePlugin?.Stop(); }
        catch { /* ignore */ }
        _activeRealtimePlugin = null;
    }
    private static string Quote(string value) =>
        value.Contains(' ') || value.Contains('\t') ? $"\"{value}\"" : value;
}
