using System.IO;
using DriveMark.Core.Execution;
using DriveMark.Core.Options;
using DriveMark.Core.Parsing;

namespace DriveMark.Tests;

/// <summary>实时绘图使用的日志增量追踪（FioLogTailer）与简单模式新增参数。</summary>
internal static class TestsLogTailer
{
    public static void Register(List<(string Name, Action Body)> tests)
    {
        tests.Add(("日志 tail：增量读取与按类型解析", () =>
        {
            var dir = TestData.CreateTempDirectory("tailer");
            try
            {
                var bw = Path.Combine(dir, "fio-gui-bw_bw.1.log");
                File.WriteAllText(bw, "100, 1024, 0, 4096, 0\n200, 2048, 1, 4096, 0\n");

                var tailer = new FioLogTailer(dir);
                var first = tailer.Poll();
                Assert.Equal(2, first.Count, "首轮应读取 2 条");
                Assert.Equal(FioLogKind.Bandwidth, first[0].Kind, "识别为带宽日志");
                Assert.Equal(0, first[0].Sample.Direction, "第一条是读方向");

                // 追加：一条完整 + 一条不完整（最后一行还没写完）
                File.AppendAllText(bw, "300, 3072, 0, 4096, 0\n400, 40");
                var second = tailer.Poll();
                Assert.Equal(1, second.Count, "不完整行不应输出");
                Assert.Approx(300, second[0].Sample.TimeMs, 1e-9, "时间应为 300ms");

                // 补全最后一行
                File.AppendAllText(bw, "96, 0, 4096, 0\n");
                var third = tailer.Poll();
                Assert.Equal(1, third.Count, "补全后应输出 1 条");
                Assert.Approx(400, third[0].Sample.TimeMs, 1e-9, "时间应为 400ms");

                Assert.Equal(0, tailer.Poll().Count, "没有新内容时应为空");
            }
            finally
            {
                TestData.TryDeleteDirectory(dir);
            }
        }));

        tests.Add(("日志 tail：只取主延迟日志，忽略 clat/slat", () =>
        {
            var dir = TestData.CreateTempDirectory("tailer-lat");
            try
            {
                File.WriteAllText(Path.Combine(dir, "x_lat.1.log"), "10, 500000, 0, 4096, 0\n");
                File.WriteAllText(Path.Combine(dir, "x_clat.1.log"), "10, 111, 0, 4096, 0\n");
                File.WriteAllText(Path.Combine(dir, "x_slat.1.log"), "10, 222, 0, 4096, 0\n");

                var samples = new FioLogTailer(dir).Poll();
                Assert.Equal(1, samples.Count, "clat/slat 应被忽略");
                Assert.Equal(FioLogKind.Latency, samples[0].Kind, "识别为延迟日志");
                Assert.Approx(500000, samples[0].Sample.Value, 1e-9, "读取主延迟值");
            }
            finally
            {
                TestData.TryDeleteDirectory(dir);
            }
        }));

        tests.Add(("日志 tail：后出现的文件从头读取", () =>
        {
            var dir = TestData.CreateTempDirectory("tailer-late");
            try
            {
                File.WriteAllText(Path.Combine(dir, "job_bw.1.log"), "100, 1024, 0, 4096, 0\n");
                var tailer = new FioLogTailer(dir);
                Assert.Equal(1, tailer.Poll().Count, "先读已有文件");

                File.WriteAllText(Path.Combine(dir, "job_iops.1.log"), "100, 10, 0, 4096, 0\n");
                var next = tailer.Poll();
                Assert.Equal(1, next.Count, "新文件应从头发送");
                Assert.Equal(FioLogKind.Iops, next[0].Kind, "识别为 IOPS 日志");
            }
            finally
            {
                TestData.TryDeleteDirectory(dir);
            }
        }));

        tests.Add(("简单模式：队列深度/队列数默认值与预设", () =>
        {
            var config = SimpleTestProfiles.CreateDefaultConfig();
            Assert.Equal("32", config.Get("iodepth"), "默认队列深度 32");
            Assert.Equal("1", config.Get("numjobs"), "默认队列数 1");

            Assert.True(SimpleTestProfiles.QueueDepthPresets.Count >= 6, "队列深度预设应齐全");
            Assert.True(SimpleTestProfiles.JobCountPresets.Count >= 4, "队列数预设应齐全");

            Assert.True(SimpleInputParser.TryParseCount("64", 1, 1024, out var value, out _), "64 应可解析");
            Assert.Equal("64", value, "解析值");
            Assert.False(SimpleInputParser.TryParseCount("0", 1, 1024, out _, out _), "0 超出范围");
            Assert.False(SimpleInputParser.TryParseCount("2000", 1, 1024, out _, out _), "2000 超出范围");
            Assert.False(SimpleInputParser.TryParseCount("abc", 1, 1024, out _, out _), "非数字应失败");
        }));
    }
}
