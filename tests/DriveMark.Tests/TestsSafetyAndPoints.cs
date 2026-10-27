using System.IO;
using DriveMark.Core.Charts;
using DriveMark.Core.Execution;
using DriveMark.Core.Options;
using DriveMark.Core.Parsing;

namespace DriveMark.Tests;

/// <summary>安全护栏、结果数据点完整性（不丢点）的测试。</summary>
internal static class TestsSafetyAndPoints
{
    public static void Register(List<(string Name, Action Body)> tests)
    {
        // ---------------- 安全：禁止清空/误删非测试目录
        tests.Add(("安全：清理器拒绝非盘符根目录", () =>
        {
            var dir = TestData.CreateTempDirectory("janitor-guard");
            var dataRoot = TestDataJanitor.GetDataRoot(dir);
            try
            {
                Directory.CreateDirectory(dataRoot);
                var keepFile = Path.Combine(dataRoot, "keep.txt");
                File.WriteAllText(keepFile, "must-not-delete");

                var report = TestDataJanitor.CleanStale(dir);

                Assert.True(report.Errors.Count > 0, "非盘符根目录应被拒绝");
                Assert.True(report.Errors.Any(e => e.Contains("拒绝清理", StringComparison.Ordinal)),
                    "错误信息应说明拒绝清理");
                Assert.True(File.Exists(keepFile), "被拒绝时不得删除任何文件");
            }
            finally
            {
                TestData.TryDeleteDirectory(dir);
            }
        }));

        tests.Add(("安全：参数校验拒绝绝对路径/盘符", () =>
        {
            var config = new FioJobConfig();
            Assert.Null(config.TrySet("filename", @"D:\some\file.dat"), "设置本身不报错（校验阶段拦截）");
            var result = config.Validate();
            Assert.True(result.HasErrors, "绝对路径应判为错误");
            Assert.True(result.Issues.Any(i => i.OptionName == "filename" &&
                                               i.Message.Contains("绝对路径", StringComparison.Ordinal)),
                "应提示禁止手填绝对路径");

            var safe = new FioJobConfig();
            safe.TrySet("filename", "test.dat");
            Assert.False(safe.Validate().HasErrors, "纯文件名应允许");
        }));

        tests.Add(("安全：系统盘写测试需要显式开关", () =>
        {
            var systemDrive = TestTargetGuard.FindCandidate(TestTargetGuard.SystemDriveRoot);
            Assert.NotNull(systemDrive, "应能识别系统盘");

            Assert.Throws<InvalidOperationException>(
                () => TestTargetGuard.EnsureWritePermission(systemDrive!, allowWrites: true, allowSystemDriveWrite: false),
                "默认应禁止系统盘写测试");

            // 显式允许后放行（界面会额外二次确认）。
            TestTargetGuard.EnsureWritePermission(systemDrive!, allowWrites: true, allowSystemDriveWrite: true);
        }));

        // ---------------- 数据点完整性
        tests.Add(("数据点：同一时间戳的不同值不会被丢掉", () =>
        {
            // fio 收尾窗口可能出现两个相同时间戳的采样，必须都保留。
            var data = new ChartDataSet("dup", "test", false,
                [
                    new FioLogSample(1000, 100, 0, 4096, 0),
                    new FioLogSample(1000, 200, 0, 4096, 0),
                    new FioLogSample(1010, 150, 0, 4096, 0),
                ],
                []);

            var model = SeriesBuilder.Build(data, XAxisDimension.ElapsedSeconds, YAxisMetric.ThroughputMebibytesPerSecond);
            Assert.Equal(1, model.Series.Count, "一条曲线");
            Assert.Equal(3, model.Series[0].Points.Count, "3 个点都应保留（不去重掉不同值）");
        }));

        tests.Add(("数据点：500 个日志采样应画出 500 个点", () =>
        {
            var samples = Enumerable.Range(0, 500)
                .Select(i => new FioLogSample(i * 10L, 100 + i % 20, 0, 4096, 0))
                .ToList();
            var data = new ChartDataSet("many", "test", false, samples, []);

            var model = SeriesBuilder.Build(data, XAxisDimension.ElapsedSeconds, YAxisMetric.ThroughputMebibytesPerSecond);
            Assert.Equal(500, model.Series[0].Points.Count, "所有唯一时间戳的点都应保留");

            var latency = new ChartDataSet("many-lat", "test", false, [], [])
            {
                LatencySamples = samples,
            };
            var latencyModel = SeriesBuilder.Build(latency, XAxisDimension.ElapsedSeconds, YAxisMetric.Latency);
            Assert.Equal(500, latencyModel.Series[0].Points.Count, "延迟曲线同样不丢点");
        }));

        tests.Add(("数据点：延迟日志末行重复时间戳也保留", () =>
        {
            var data = new ChartDataSet("lat-dup", "test", false, [], [])
            {
                LatencySamples =
                [
                    new FioLogSample(9990, 872212, 0, 4096, 0),
                    new FioLogSample(10000, 882869, 0, 4096, 0),
                    new FioLogSample(10000, 1216742, 0, 4096, 0),
                ],
            };
            var model = SeriesBuilder.Build(data, XAxisDimension.ElapsedSeconds, YAxisMetric.Latency);
            Assert.Equal(3, model.Series[0].Points.Count, "同时间戳不同延迟值应保留");
        }));
    }
}
