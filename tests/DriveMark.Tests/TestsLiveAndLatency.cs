using System.IO;
using DriveMark.Core.Charts;
using DriveMark.Core.Options;
using DriveMark.Core.Parsing;

namespace DriveMark.Tests;

/// <summary>实时绘图缓冲、延迟(LAT)纵轴、时间轴零起点 的测试。</summary>
internal static class TestsLiveAndLatency
{
    public static void Register(List<(string Name, Action Body)> tests)
    {
        // ---------------- 实时绘图缓冲
        tests.Add(("实时绘图：启动阶段无速率的行不计入数据", () =>
        {
            var buffer = new LiveChartBuffer();
            var added = buffer.Add(TimeSpan.FromSeconds(1), new ProgressSample(null, 10, null, null, null, null, null));
            Assert.False(added, "布局/启动阶段的进度行不应被记录");
            Assert.Equal(0, buffer.Count, "缓冲区应为空");
        }));

        tests.Add(("实时绘图：横轴从第一个数据点起算", () =>
        {
            var buffer = new LiveChartBuffer();
            // 模拟：启动等了 8 秒才开始有速率数据
            buffer.Add(TimeSpan.FromSeconds(8), Sample(readMibps: 100, readIops: 1000));
            buffer.Add(TimeSpan.FromSeconds(9), Sample(readMibps: 150, readIops: 1500));
            buffer.Add(TimeSpan.FromSeconds(10), Sample(readMibps: 120, readIops: 1200));

            var model = buffer.Build(XAxisDimension.ElapsedSeconds, YAxisMetric.ThroughputMebibytesPerSecond,
                ChartRenderStyle.Line);
            Assert.Equal(1, model.Series.Count, "应只有读曲线");
            var points = model.Series[0].Points;
            Assert.Equal(3, points.Count, "3 个数据点");
            Assert.Approx(0, points[0].X, 1e-9, "第一个点 X 应为 0（等待时间不计入）");
            Assert.Approx(1, points[1].X, 1e-9, "第二个点 X=1 秒");
            Assert.Approx(100, points[0].Y, 1e-9, "吞吐量原值");
        }));

        tests.Add(("实时绘图：切到 IOPS 与传输量横轴", () =>
        {
            var buffer = new LiveChartBuffer();
            buffer.Add(TimeSpan.FromSeconds(0), Sample(readMibps: 100, readIops: 1000));
            buffer.Add(TimeSpan.FromSeconds(1), Sample(readMibps: 200, readIops: 2000));

            var iops = buffer.Build(XAxisDimension.ElapsedSeconds, YAxisMetric.Iops, ChartRenderStyle.Scatter);
            Assert.Approx(2000, iops.Series[0].Points[1].Y, 1e-9, "IOPS 值");
            Assert.Equal(ChartRenderStyle.Scatter, iops.RenderStyle, "散点样式");

            var volume = buffer.Build(XAxisDimension.TransferredMebibytes, YAxisMetric.ThroughputMebibytesPerSecond,
                ChartRenderStyle.Line);
            // 100 与 200 MiB/s 相隔 1 秒 → 累计 (100+200)/2 = 150 MiB
            Assert.Approx(150, volume.Series[0].Points[1].X, 1e-9, "累计传输量");
        }));

        tests.Add(("实时绘图：延迟纵轴给出提示", () =>
        {
            var buffer = new LiveChartBuffer();
            buffer.Add(TimeSpan.FromSeconds(0), Sample(readMibps: 100, readIops: 1000));
            var model = buffer.Build(XAxisDimension.ElapsedSeconds, YAxisMetric.Latency, ChartRenderStyle.Line);
            Assert.False(model.HasData, "实时模式不支持延迟");
            Assert.NotNull(model.EmptyMessage, "应有提示");
            Assert.Contains(model.EmptyMessage!, "不支持延迟", "提示应说明原因");
            Assert.False(LiveChartBuffer.SupportsMetric(YAxisMetric.Latency), "SupportsMetric 应返回 false");
        }));

        tests.Add(("实时绘图：窗口范围与清空", () =>
        {
            var buffer = new LiveChartBuffer();
            for (var i = 0; i < 10; i++)
                buffer.Add(TimeSpan.FromSeconds(i), Sample(readMibps: 100 + i, readIops: 1000 + i));

            var span = buffer.ComputeSpan(XAxisDimension.ElapsedSeconds);
            Assert.Approx(9, span, 1e-9, "数据跨度应为 9 秒");

            var model = buffer.Build(XAxisDimension.ElapsedSeconds, YAxisMetric.ThroughputMebibytesPerSecond,
                ChartRenderStyle.Line, windowSeconds: 4);
            var range = model.ResolveX();
            Assert.Approx(5, range.Min, 1e-9, "窗口起点=9-4");
            Assert.Approx(9, range.Max, 1e-9, "窗口终点=最新数据");

            buffer.Clear();
            Assert.False(buffer.HasData, "清空后无数据");
            Assert.Equal(0, buffer.Count, "清空后计数为 0");
        }));

        // ---------------- 延迟纵轴（完成后绘图）
        tests.Add(("延迟纵轴：纳秒日志换算为微秒", () =>
        {
            var data = new ChartDataSet("lat", "test", false, [], [])
            {
                LatencySamples =
                [
                    new FioLogSample(1000, 500_000, 0, 4096, 0),  // 0.5ms
                    new FioLogSample(2000, 800_000, 0, 4096, 0),  // 0.8ms
                ],
            };

            var model = SeriesBuilder.Build(data, XAxisDimension.ElapsedSeconds, YAxisMetric.Latency);
            Assert.True(model.HasData, "应能画出延迟");
            Assert.Contains(model.YTitle, "µs", "单位应为微秒");
            Assert.Equal("µs", model.YUnit, "图例单位");
            Assert.Approx(500, model.Series[0].Points[0].Y, 1e-9, "500000ns = 500µs");
        }));

        tests.Add(("延迟纵轴：数值较大时自动换算为毫秒", () =>
        {
            var data = new ChartDataSet("lat", "test", false, [], [])
            {
                LatencySamples =
                [
                    new FioLogSample(1000, 3_000_000, 0, 4096, 0),  // 3ms
                    new FioLogSample(2000, 5_000_000, 0, 4096, 0),  // 5ms
                ],
            };

            var model = SeriesBuilder.Build(data, XAxisDimension.ElapsedSeconds, YAxisMetric.Latency);
            Assert.Contains(model.YTitle, "ms", "大延迟应使用毫秒");
            Assert.Equal("ms", model.YUnit, "图例单位");
            Assert.Approx(3, model.Series[0].Points[0].Y, 1e-9, "3ms");
        }));

        tests.Add(("延迟纵轴：缺少 lat 日志时提示", () =>
        {
            var data = ChartDataSet.Empty("空", "无");
            var model = SeriesBuilder.Build(data, XAxisDimension.ElapsedSeconds, YAxisMetric.Latency);
            Assert.False(model.HasData, "无数据");
            Assert.Contains(model.EmptyMessage!, "write_lat_log", "提示应包含 fio 参数名");
        }));

        // ---------------- 时间轴零起点（完成后绘图也排除启动等待）
        tests.Add(("完成绘图：时间轴可选从第一个数据点起算", () =>
        {
            var data = FioLogDataSetLoader.FromFiles(
                "demo", "合成",
                TestData.DemoLog("ssd-mixed_bw.1.log"),
                TestData.DemoLog("ssd-mixed_iops.1.log"),
                isSynthetic: true);

            var normal = SeriesBuilder.Build(data, XAxisDimension.ElapsedSeconds, YAxisMetric.ThroughputMebibytesPerSecond);
            var readNormal = normal.Series.First(s => s.Role == SeriesRole.Read);
            Assert.Approx(1, readNormal.Points[0].X, 1e-9, "默认仍从日志时间 0 起");

            var zero = SeriesBuilder.Build(data, XAxisDimension.ElapsedSeconds, YAxisMetric.ThroughputMebibytesPerSecond,
                zeroBasedTimeAxis: true);
            var readZero = zero.Series.First(s => s.Role == SeriesRole.Read);
            Assert.Approx(0, readZero.Points[0].X, 1e-9, "零起点后第一个点为 0");

            // 传输量横轴不受时间零起点影响
            var volume = SeriesBuilder.Build(data, XAxisDimension.TransferredMebibytes,
                YAxisMetric.ThroughputMebibytesPerSecond, zeroBasedTimeAxis: true);
            Assert.True(volume.Series[0].Points[0].X > 0, "传输量横轴仍为累计量");
        }));

        // ---------------- 日志加载：延迟文件
        tests.Add(("日志加载：识别并读取 _lat 日志（不误读 _clat/_slat）", () =>
        {
            var dir = TestData.CreateTempDirectory("latload");
            try
            {
                File.WriteAllText(Path.Combine(dir, "job.fio"), "[global]\nrw=randread\nbs=4k\n\n[job]\n");
                File.WriteAllText(Path.Combine(dir, "x_bw.1.log"), "1000, 1000, 0, 4096, 0\n");
                File.WriteAllText(Path.Combine(dir, "x_iops.1.log"), "1000, 10, 0, 4096, 0\n");
                File.WriteAllText(Path.Combine(dir, "x_lat.1.log"), "1000, 500000, 0, 4096, 0\n2000, 600000, 0, 4096, 0\n");
                File.WriteAllText(Path.Combine(dir, "x_clat.1.log"), "1000, 111, 0, 4096, 0\n");
                File.WriteAllText(Path.Combine(dir, "x_slat.1.log"), "1000, 222, 0, 4096, 0\n");

                var data = FioLogDataSetLoader.FromDirectory(dir);
                Assert.True(data.HasLatency, "应加载到延迟日志");
                Assert.Equal(2, data.LatencySamples.Count, "只应读取 _lat 文件（2 行）");
                Assert.Approx(500_000, data.LatencySamples[0].Value, 1e-9, "读取的是 _lat 的值");
            }
            finally
            {
                TestData.TryDeleteDirectory(dir);
            }
        }));

        // ---------------- 默认配置包含延迟日志参数
        tests.Add(("默认配置与预设：开启 write_lat_log", () =>
        {
            var config = SimpleTestProfiles.CreateDefaultConfig();
            Assert.Equal("fio-gui-lat", config.Get("write_lat_log"), "默认配置应输出延迟日志");

            foreach (var preset in FioPresets.All.Where(p => p.Name.StartsWith("图表采集", StringComparison.Ordinal)))
                Assert.Equal("fio-gui-lat", preset.Values["write_lat_log"], $"{preset.Name} 应输出延迟日志");
        }));
    }

    private static ProgressSample Sample(double? readMibps = null, double? readIops = null)
        => new(null, null, readMibps, readIops, null, null, null);
}
