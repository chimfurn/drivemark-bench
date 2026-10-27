using System.IO;
using DriveMark.Core.Charts;
using DriveMark.Core.Execution;
using DriveMark.Core.Options;
using DriveMark.Core.Parsing;

namespace DriveMark.Tests;

/// <summary>fio 执行层（目标闸门、参数构建、数据清理、单位解析）的测试。</summary>
internal static class TestsExecution
{
    public static void Register(List<(string Name, Action Body)> tests)
    {
        // ---------------- 目标安全闸门
        tests.Add(("目标盘：路径归一化为盘符", () =>
        {
            var driveRoot = TestTargetGuard.SystemDriveRoot;
            Assert.Equal(driveRoot, TestTargetGuard.NormalizeDriveRoot(driveRoot + "foo\\bar"), "子目录归一化为盘符");
            Assert.Equal(driveRoot, TestTargetGuard.NormalizeDriveRoot(driveRoot.TrimEnd('\\')), "裸盘符归一化");
        }));

        tests.Add(("目标盘枚举：只返回本地/可移动磁盘", () =>
        {
            var candidates = TestTargetGuard.EnumerateCandidates();
            Assert.True(candidates.Count >= 1, "至少应枚举到系统盘");
            foreach (var candidate in candidates)
            {
                Assert.True(candidate.TotalBytes > 0, $"{candidate.DriveLetter} 应有容量");
                Assert.True(candidate.DriveType is DriveType.Fixed or DriveType.Removable,
                    $"{candidate.DriveLetter} 应为本地盘或可移动盘");
            }

            // 排序规则：非系统盘应排在系统盘之前。
            var firstSystemIndex = candidates.ToList().FindIndex(c => c.IsSystemDrive);
            if (firstSystemIndex >= 0)
            {
                Assert.True(candidates.Take(firstSystemIndex).All(c => !c.IsSystemDrive),
                    "非系统盘应优先出现在列表前面");
            }
        }));

        tests.Add(("目标盘校验：系统盘可只读测试，但禁止写入测试", () =>
        {
            var systemDrive = TestTargetGuard.FindCandidate(TestTargetGuard.SystemDriveRoot);
            Assert.NotNull(systemDrive, "系统盘应能被识别");
            Assert.True(systemDrive!.IsSystemDrive, "应标记为系统盘");

            // 注意：只读测试根本不会调用写权限检查（由调用方按 rw 判断）。

            // 写测试默认被拒绝。
            Assert.Throws<InvalidOperationException>(
                () => TestTargetGuard.EnsureWritePermission(systemDrive, allowWrites: true, allowSystemDriveWrite: false),
                "系统盘写测试应被拒绝");

            // 显式允许（仅命令行高级开关）时放行。
            TestTargetGuard.EnsureWritePermission(systemDrive, allowWrites: true, allowSystemDriveWrite: true);
        }));

        tests.Add(("目标盘校验：非系统盘允许写入测试", () =>
        {
            var nonSystem = TestTargetGuard.EnumerateCandidates().FirstOrDefault(c => !c.IsSystemDrive);
            if (nonSystem is null)
            {
                Console.WriteLine("      （跳过：本机没有非系统盘）");
                return;
            }

            TestTargetGuard.EnsureWritePermission(nonSystem, allowWrites: true, allowSystemDriveWrite: false);
            Assert.False(nonSystem.IsSystemDrive, "非系统盘不应被标记为系统盘");
        }));

        tests.Add(("目标盘校验：无效盘符被拒绝", () =>
        {
            Assert.Throws<InvalidOperationException>(
                () => TestTargetGuard.Inspect(@"Z:\"), "不存在的盘符应被拒绝");
            Assert.Throws<InvalidOperationException>(
                () => TestTargetGuard.Inspect(@"\\server\share"), "网络路径应被拒绝");
        }));

        tests.Add(("目标盘校验：可用白名单限制", () =>
        {
            var candidates = TestTargetGuard.EnumerateCandidates();
            var systemRoot = TestTargetGuard.SystemDriveRoot;
            var nonSystem = candidates.FirstOrDefault(c => !c.IsSystemDrive);

            // 白名单只放系统盘：系统盘通过，其它盘被拒。
            TestTargetGuard.Inspect(systemRoot, [systemRoot]);
            if (nonSystem is not null)
            {
                Assert.Throws<InvalidOperationException>(
                    () => TestTargetGuard.Inspect(nonSystem.RootPath, [systemRoot]),
                    "白名单之外的盘应被拒绝");
            }
        }));

        tests.Add(("目标盘校验：系统盘（存在则可用）", () =>
        {
            var driveRoot = TestTargetGuard.SystemDriveRoot;
            if (!Directory.Exists(driveRoot))
            {
                Console.WriteLine("      （跳过：本机没有系统盘根目录）");
                return;
            }

            var info = TestTargetGuard.Inspect(driveRoot);
            Assert.Equal(driveRoot[0].ToString(), info.DriveLetter, "盘符");
            Assert.True(info.FreeBytes >= 0, "可用空间应非负");
        }));

        tests.Add(("目标闸门：空间不足时拒绝", () =>
        {
            var driveRoot = TestTargetGuard.SystemDriveRoot;
            var tiny = new TestTargetInfo(driveRoot, driveRoot[0].ToString(), "test", "NTFS", 1000, 100);
            Assert.Throws<InvalidOperationException>(
                () => TestTargetGuard.EnsureCapacity(tiny, 1024L * 1024 * 1024), "空间不足应抛异常");

            var roomy = new TestTargetInfo(driveRoot, driveRoot[0].ToString(), "test", "NTFS", long.MaxValue, long.MaxValue);
            TestTargetGuard.EnsureCapacity(roomy, 64L * 1024 * 1024); // 不应抛异常
        }));

        // ---------------- 尺寸/时间解析
        tests.Add(("尺寸解析：常见 fio 写法", () =>
        {
            Assert.Equal(4096L, FioValueParser.ParseSizeBytes("4096")!.Value, "纯数字=字节");
            Assert.Equal(4096L, FioValueParser.ParseSizeBytes("4k")!.Value, "k=1024");
            Assert.Equal(1024L * 1024 * 1024, FioValueParser.ParseSizeBytes("1g")!.Value, "g");
            Assert.Equal(1024L, FioValueParser.ParseSizeBytes("1KiB")!.Value, "KiB");
            Assert.Null(FioValueParser.ParseSizeBytes("abc"), "非法值返回 null");
        }));

        tests.Add(("时间解析：常见 fio 写法", () =>
        {
            Assert.Equal(TimeSpan.FromSeconds(10), FioValueParser.ParseDuration("10s")!.Value, "10s");
            Assert.Equal(TimeSpan.FromSeconds(3), FioValueParser.ParseDuration("3")!.Value, "纯数字按秒");
            Assert.Equal(TimeSpan.FromMilliseconds(500), FioValueParser.ParseDuration("500ms")!.Value, "ms");
            Assert.Equal(TimeSpan.FromMinutes(2), FioValueParser.ParseDuration("2m")!.Value, "m");
            Assert.Null(FioValueParser.ParseDuration("xyz"), "非法值返回 null");
        }));

        tests.Add(("写模式识别", () =>
        {
            Assert.False(FioValueParser.IsWriteMode("read"), "read 不是写");
            Assert.False(FioValueParser.IsWriteMode("randread"), "randread 不是写");
            Assert.True(FioValueParser.IsWriteMode("randwrite"), "randwrite 是写");
            Assert.True(FioValueParser.IsWriteMode("rw"), "rw 是写");
            Assert.True(FioValueParser.IsWriteMode("randrw"), "randrw 是写");
            Assert.True(FioValueParser.IsWriteMode("trimwrite"), "trimwrite 是写");
        }));

        // ---------------- 命令行构建
        tests.Add(("fio 命令行：进度/JSON/线程参数齐全且 job 文件在最后", () =>
        {
            var config = FioPresets.CreateDefault();
            var args = FioRunner.BuildFioArguments(config, @"D:\runs\x\job.fio", "result.json", 1000);

            Assert.True(args.Contains("--eta=always"), "必需 --eta=always 才有实时进度");
            Assert.True(args.Contains("--output-format=json"), "必需 JSON 输出");
            Assert.True(args.Contains("--output=result.json"), "结果写到相对路径（避免盘符冒号问题）");
            Assert.True(args.Contains("--thread=1"), "应显式线程模式");

            // fio 的时间选项必须带单位后缀：裸数字会被当成"秒"，导致 eta 行被静默关闭（实测）。
            var etaInterval = args.FirstOrDefault(a => a.StartsWith("--eta-interval=", StringComparison.Ordinal));
            Assert.NotNull(etaInterval, "应设置 --eta-interval 以控制实时进度频率");
            Assert.True(etaInterval!.EndsWith("ms", StringComparison.Ordinal),
                $"eta-interval 必须带 ms 单位，实际：{etaInterval}");
            Assert.False(args.Any(a => a.StartsWith("--status-interval=", StringComparison.Ordinal)),
                "不应同时传 --status-interval（实测会让 eta 行消失）");

            Assert.Equal(@"D:\runs\x\job.fio", args[^1], "job 文件必须是最后一个位置参数");
        }));

        tests.Add(("fio 命令行：保留 readonly 安全开关", () =>
        {
            var config = FioPresets.CreateDefault();
            config.TrySet("readonly", "1");
            var args = FioRunner.BuildFioArguments(config, "job.fio", "result.json", 1000);
            Assert.True(args.Contains("--readonly"), "只读开关应保留");
        }));

        tests.Add(("fio 命令行：去掉与固定输出冲突的重复选项", () =>
        {
            var config = FioPresets.CreateDefault();
            config.TrySet("output-format", "normal");
            config.TrySet("eta", "never");
            var args = FioRunner.BuildFioArguments(config, "job.fio", "result.json", 1000);

            Assert.Equal(1, args.Count(a => a.StartsWith("--output-format", StringComparison.Ordinal)),
                "--output-format 不应重复");
            Assert.False(args.Contains("--eta=never"), "配置里的 eta 应被移除");
        }));

        tests.Add(("测试文件名校验：拒绝带路径或冒号的名字", () =>
        {
            FioRunner.EnsureValidTestFileName("fio-test.dat"); // 不应抛异常
            Assert.Throws<InvalidOperationException>(() => FioRunner.EnsureValidTestFileName("X:/x.dat"), "冒号");
            Assert.Throws<InvalidOperationException>(() => FioRunner.EnsureValidTestFileName("dir\\x.dat"), "反斜杠");
            Assert.Throws<InvalidOperationException>(() => FioRunner.EnsureValidTestFileName("dir/x.dat"), "斜杠");
            Assert.Throws<InvalidOperationException>(() => FioRunner.EnsureValidTestFileName(""), "空名");
        }));

        // ---------------- 残留清理
        tests.Add(("数据清理：只删除目标盘的 DriveMark-test 目录", () =>
        {
            var root = TestData.CreateTempDirectory("janitor");
            var dataRoot = TestDataJanitor.GetDataRoot(root);
            var staleDir = Path.Combine(dataRoot, "20260101-000000-abcd");
            var keepFile = Path.Combine(root, "keep-me.txt");

            try
            {
                Directory.CreateDirectory(staleDir);
                File.WriteAllBytes(Path.Combine(staleDir, "fio-test.dat"), new byte[4096]);
                File.WriteAllBytes(Path.Combine(staleDir, "fio-gui-bw_bw.1.log"), new byte[64]);
                File.WriteAllText(keepFile, "keep");

                var report = TestDataJanitor.CleanStaleForTests(root);

                Assert.True(report.HadWork, "应报告清理了内容");
                Assert.Equal(1, report.RemovedDirectories, "删除 1 个目录");
                Assert.Equal(2, report.RemovedFiles, "删除 2 个文件");
                Assert.True(report.RemovedBytes >= 4160, $"释放字节数应 >= 4160（实际 {report.RemovedBytes}）");
                Assert.False(Directory.Exists(dataRoot), "DriveMark-test 目录应被删除");
                Assert.True(File.Exists(keepFile), "DriveMark-test 之外的文件必须保留");
            }
            finally
            {
                TestData.TryDeleteDirectory(root);
            }
        }));

        tests.Add(("数据清理：目录不存在时安全返回", () =>
        {
            var root = Path.Combine(TestData.Root, "runtime", "test-tmp", "janitor-none-" + Guid.NewGuid().ToString("N")[..8]);
            var report = TestDataJanitor.CleanStaleForTests(root);
            Assert.False(report.HadWork, "没有残留时不应报告工作");
            Assert.Equal(0, report.Errors.Count, "不应有错误");
        }));

        // ---------------- 运行目录的 job 摘要
        tests.Add(("日志目录：从 job.fio 推导可读标题", () =>
        {
            var dir = TestData.CreateTempDirectory("jobsummary");
            try
            {
                File.WriteAllText(Path.Combine(dir, "job.fio"), """
                    # generated
                    [global]
                    rw=randread
                    bs=4k
                    iodepth=32

                    [job]
                    """);
                File.WriteAllText(Path.Combine(dir, "x_bw.1.log"), "1000, 1000, 0, 4096, 0\n2000, 2000, 0, 4096, 0\n");
                File.WriteAllText(Path.Combine(dir, "x_iops.1.log"), "1000, 10, 0, 4096, 0\n2000, 20, 0, 4096, 0\n");

                var data = FioLogDataSetLoader.FromDirectory(dir);
                Assert.Contains(data.Name, "randread", "标题应含 rw");
                Assert.Contains(data.Name, "4k", "标题应含 bs");
                Assert.Contains(data.Name, "QD32", "标题应含队列深度");
                Assert.True(data.HasBandwidth && data.HasIops, "两类日志都应加载");
            }
            finally
            {
                TestData.TryDeleteDirectory(dir);
            }
        }));

        // ---------------- 结果展示
        tests.Add(("运行结果：指标行包含读写性能", () =>
        {
            var json = FioJsonParser.ParseFile(TestData.BenchSample("fio-json-sample.json"));
            var result = new FioRunResult
            {
                RunDirectory = "x",
                CommandLine = "fio",
                TestTargetDescription = "E 盘",
                ExitCode = 0,
                Duration = TimeSpan.FromSeconds(1),
                Json = json,
                DataSet = ChartDataSet.Empty("x", "y"),
            };

            var lines = result.BuildMetricLines();
            Assert.True(lines.Count >= 2, "应有版本与性能行");
            Assert.True(lines.Any(l => l.StartsWith("读：", StringComparison.Ordinal)), "应包含读性能行");
            Assert.True(result.Succeeded, "退出码 0 且无错误应判定成功");
        }));

        // ---------------- 简单模式（面向普通用户）
        tests.Add(("简单模式：默认配置为随机读 + 256MB + 10 秒 + 10ms 采样", () =>
        {
            var config = SimpleTestProfiles.CreateDefaultConfig();
            Assert.Equal("randread", config.Get("rw"), "默认随机读（不写盘）");
            Assert.Equal("4k", config.Get("bs"), "默认 4K");
            Assert.Equal("256m", config.Get("size"), "默认 256MB");
            Assert.Equal("10s", config.Get("runtime"), "默认 10 秒");
            Assert.Equal("10", config.Get("log_avg_msec"), "默认 10ms 采样（约每秒 100 次）");
            Assert.Equal("10", config.Get("bwavgtime"), "bw 窗口同步为 10ms");
            Assert.Equal("10", config.Get("iopsavgtime"), "iops 窗口同步为 10ms");
            Assert.False(config.Validate().HasErrors, "默认简单配置不应有错误");
        }));

        tests.Add(("简单模式：切换测试项目会写入正确参数", () =>
        {
            var config = SimpleTestProfiles.CreateDefaultConfig();

            var mixed = SimpleTestProfiles.Find("混合读写")!;
            SimpleTestProfiles.ApplyProfile(config, mixed);
            Assert.Equal("randrw", config.Get("rw"), "混合读写应为 randrw");
            Assert.Equal("70", config.Get("rwmixread"), "混合读写应为 70% 读");
            Assert.True(mixed.WritesData, "混合读写应标记为写盘");

            var read = SimpleTestProfiles.Find("顺序读")!;
            SimpleTestProfiles.ApplyProfile(config, read);
            Assert.Equal("read", config.Get("rw"), "顺序读");
            Assert.Equal("1m", config.Get("bs"), "顺序读块大小 1M");
            Assert.Null(config.Get("rwmixread"), "切回只读时应清除混合比例");
        }));

        tests.Add(("简单模式：采样间隔预设齐全且能写入配置", () =>
        {
            var presets = SimpleTestProfiles.SamplingPresets;
            Assert.True(presets.Any(p => p.Milliseconds == 10), "应包含 10ms（每秒 100 次）");
            Assert.True(presets.Any(p => p.Milliseconds == 1000), "应包含 1 秒");
            Assert.Equal(10, SimpleTestProfiles.DefaultSamplingMilliseconds, "默认 10ms");

            var config = new FioJobConfig();
            SimpleTestProfiles.ApplySampling(config, 50);
            Assert.Equal("50", config.Get("log_avg_msec"), "log_avg_msec");
            Assert.Equal("50", config.Get("bwavgtime"), "bwavgtime");
            Assert.Equal("50", config.Get("iopsavgtime"), "iopsavgtime");
        }));
    }
}
