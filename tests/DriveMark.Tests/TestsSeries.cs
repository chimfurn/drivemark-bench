using DriveMark.Core.Charts;
using DriveMark.Core.Parsing;

namespace DriveMark.Tests;

/// <summary>累计传输量积分与图表装配（SeriesBuilder）的测试。</summary>
internal static class TestsSeries
{
    public static void Register(List<(string Name, Action Body)> tests)
    {
        // ---------------- VolumeIntegrator
        tests.Add(("积分：已知三点的梯形积分", () =>
        {
            var integrator = VolumeIntegrator.FromBandwidth(new List<FioLogSample>
            {
                new(1000, 100, 0, 4096, 0), // 100 KiB/s @1s
                new(2000, 200, 0, 4096, 0),
                new(3000, 300, 0, 4096, 0),
            });
            // 100 KiB/s × 1s = 100 KiB；再 +150 KiB；再 +250 KiB = 500 KiB = 0.48828125 MiB
            Assert.Approx(500 / 1024d, integrator.TotalMib, 1e-9, "总累计量");
            Assert.Approx(100 / 1024d, integrator.CumulativeAt(1000), 1e-9, "1s 处累计量");
            Assert.Approx(250 / 1024d, integrator.CumulativeAt(2000), 1e-9, "2s 处累计量");
            // 2.5s 处：250 KiB + 插值速率 250 KiB/s × 0.5s = 375 KiB
            Assert.Approx(375 / 1024d, integrator.CumulativeAt(2500), 1e-9, "2.5s 处累计量");
        }));

        tests.Add(("积分：边界与外推", () =>
        {
            var integrator = VolumeIntegrator.FromBandwidth(new List<FioLogSample>
            {
                new(1000, 100, 0, 4096, 0),
                new(3000, 300, 0, 4096, 0),
            });
            Assert.Approx(0, integrator.CumulativeAt(0), 1e-9, "0 时刻为 0");
            Assert.Approx(0, integrator.CumulativeAt(-500), 1e-9, "负时刻为 0");
            // 终点之后再外推 1s：总 500 KiB + 300 KiB = 800 KiB
            Assert.Approx(800 / 1024d, integrator.CumulativeAt(4000), 1e-9, "末端外推");
        }));

        tests.Add(("积分：空数据与 IOPS 换算", () =>
        {
            Assert.True(VolumeIntegrator.Empty.IsEmpty, "空积分器");
            Assert.Approx(0, VolumeIntegrator.Empty.CumulativeAt(5000), 1e-9, "空积分器查询为 0");
            Assert.Approx(0, VolumeIntegrator.Empty.TotalMib, 1e-9, "空积分器总量为 0");

            // IOPS × 块大小 换算：100 IOPS × 4096B = 0.390625 MiB/s，@2s → 0.78125 MiB
            var fromIops = VolumeIntegrator.FromIops(new List<FioLogSample>
            {
                new(2000, 100, 0, 4096, 0),
            });
            Assert.Approx(0.78125, fromIops.TotalMib, 1e-9, "IOPS 换算累计量");
        }));

        // ---------------- SeriesBuilder
        tests.Add(("图表：时间轴 × 吞吐量", () =>
        {
            var data = LoadSsd();
            var model = SeriesBuilder.Build(data, XAxisDimension.ElapsedSeconds, YAxisMetric.ThroughputMebibytesPerSecond);
            Assert.Equal(2, model.Series.Count, "读写两条曲线");
            var read = model.Series.First(s => s.Role == SeriesRole.Read);
            Assert.Equal(60, read.Count, "60 个采样点");
            Assert.Approx(1.0, read.Points[0].X, 1e-9, "首个点在 1 秒");
            var firstBw = data.BandwidthSamples.First(s => s.Direction == 0);
            Assert.Approx(firstBw.Value / 1024d, read.Points[0].Y, 1e-9, "KiB/s 应换算为 MiB/s");
            Assert.Contains(model.XTitle, "时间", "X 轴标题");
            Assert.Contains(model.YTitle, "MiB/s", "Y 轴标题");
        }));

        tests.Add(("图表：时间轴 × IOPS", () =>
        {
            var data = LoadSsd();
            var model = SeriesBuilder.Build(data, XAxisDimension.ElapsedSeconds, YAxisMetric.Iops);
            var read = model.Series.First(s => s.Role == SeriesRole.Read);
            var firstIops = data.IopsSamples.First(s => s.Direction == 0);
            Assert.Approx(firstIops.Value, read.Points[0].Y, 1e-9, "IOPS 原样使用");
            Assert.Equal("IOPS", model.YTitle, "Y 轴标题");
        }));

        tests.Add(("图表：传输量轴 × IOPS（横轴用累计写入/读取量）", () =>
        {
            var data = LoadSsd();
            var model = SeriesBuilder.Build(data, XAxisDimension.TransferredMebibytes, YAxisMetric.Iops);
            var read = model.Series.First(s => s.Role == SeriesRole.Read);

            // 单调递增：累计量只能变大
            for (var i = 1; i < read.Points.Count; i++)
                Assert.True(read.Points[i].X >= read.Points[i - 1].X, $"第 {i} 点横轴应单调不减");

            var firstBw = data.BandwidthSamples.First(s => s.Direction == 0);
            // 传输量超过 2048 MiB 时横轴会自动从 MiB 换算为 GiB，断言时按标题单位折算。
            var unitScale = model.XTitle.Contains("GiB", StringComparison.Ordinal) ? 1024d : 1d;
            Assert.Approx(firstBw.Value / 1024d / unitScale, read.Points[0].X, 1e-6,
                "首点横轴 = 首秒传输量（按轴单位折算）");

            // 总量超过 2048 MiB 时应自动升到 GiB
            Assert.Contains(model.XTitle, "GiB", "大传输量应使用 GiB");
            var last = read.Points[^1].X;
            Assert.True(last is > 15 and < 40, $"读累计量应落在合理区间（实际 {last:F2} GiB）");
        }));

        tests.Add(("图表：写序列的传输量小于读序列", () =>
        {
            var data = LoadSsd();
            var model = SeriesBuilder.Build(data, XAxisDimension.TransferredMebibytes, YAxisMetric.Iops);
            var readMax = model.Series.First(s => s.Role == SeriesRole.Read).Points[^1].X;
            var writeMax = model.Series.First(s => s.Role == SeriesRole.Write).Points[^1].X;
            Assert.True(writeMax < readMax, "写累计量应小于读累计量");
        }));

        tests.Add(("图表：手动坐标轴不被 GiB 自动换算覆盖", () =>
        {
            var data = LoadSsd();
            var model = SeriesBuilder.Build(
                data, XAxisDimension.TransferredMebibytes, YAxisMetric.Iops,
                xAxis: AxisSpec.Manual(0, 30, 5));
            Assert.Contains(model.XTitle, "MiB", "手动轴应保持 MiB 单位");
            var range = model.ResolveX();
            Assert.Approx(0, range.Min, 1e-9, "手动最小值");
            Assert.Approx(30, range.Max, 1e-9, "手动最大值");
            Assert.Approx(5, range.Step, 1e-9, "手动步长");
        }));

        tests.Add(("图表：只有 IOPS 日志时也能算出传输量", () =>
        {
            var data = new ChartDataSet("only-iops", "test", false, [], new List<FioLogSample>
            {
                new(1000, 100, 0, 4096, 0),
                new(2000, 200, 0, 4096, 0),
            });
            var model = SeriesBuilder.Build(data, XAxisDimension.TransferredMebibytes, YAxisMetric.Iops);
            Assert.Equal(1, model.Series.Count, "一条曲线");
            Assert.Approx(0.9765625, model.Series[0].Points[^1].X, 1e-9, "IOPS×块大小推出的累计量");
        }));

        tests.Add(("图表：空数据集给出可读提示", () =>
        {
            var model = SeriesBuilder.Build(ChartDataSet.Empty("空", "无数据"), XAxisDimension.ElapsedSeconds, YAxisMetric.Iops);
            Assert.False(model.HasData, "不应有数据");
            Assert.NotNull(model.EmptyMessage, "应有提示文本");
            Assert.Contains(model.EmptyMessage!, "write_iops_log", "提示应指出缺少的 fio 参数");
        }));

        tests.Add(("图表：缺少对应日志时的提示", () =>
        {
            var bwOnly = new ChartDataSet("bw", "test", false, new List<FioLogSample>
            {
                new(1000, 1000, 0, 4096, 0),
            }, []);

            var iopsModel = SeriesBuilder.Build(bwOnly, XAxisDimension.ElapsedSeconds, YAxisMetric.Iops);
            Assert.False(iopsModel.HasData, "缺 iops 日志时不应有数据");
            Assert.Contains(iopsModel.EmptyMessage!, "write_iops_log", "应提示 write_iops_log");

            var bwModel = SeriesBuilder.Build(bwOnly, XAxisDimension.ElapsedSeconds, YAxisMetric.ThroughputMebibytesPerSecond);
            Assert.True(bwModel.HasData, "有 bw 日志时应能画出吞吐量");
        }));

        tests.Add(("图表：HDD 数据（单写序列）", () =>
        {
            var data = FioLogDataSetLoader.FromFiles(
                "hdd",
                "test",
                TestData.DemoLog("hdd-write_bw.1.log"),
                TestData.DemoLog("hdd-write_iops.1.log"),
                isSynthetic: true);

            var model = SeriesBuilder.Build(data, XAxisDimension.ElapsedSeconds, YAxisMetric.Iops);
            Assert.Equal(1, model.Series.Count, "只有写序列");
            Assert.Equal(SeriesRole.Write, model.Series[0].Role, "角色为写");
            var range = model.ResolveY();
            Assert.True(range.Max <= 300, $"HDD IOPS 轴不应超过 300（实际 {range.Max}）");
        }));

        tests.Add(("图表：散点图与折线图可切换", () =>
        {
            var data = LoadSsd();
            var line = SeriesBuilder.Build(data, XAxisDimension.ElapsedSeconds, YAxisMetric.Iops);
            Assert.Equal(ChartRenderStyle.Line, line.RenderStyle, "默认应为折线图");

            var scatter = SeriesBuilder.Build(data, XAxisDimension.ElapsedSeconds, YAxisMetric.Iops,
                renderStyle: ChartRenderStyle.Scatter);
            Assert.Equal(ChartRenderStyle.Scatter, scatter.RenderStyle, "应切换为散点图");
            Assert.Equal(line.Series.Count, scatter.Series.Count, "两种样式的数据曲线应一致");
        }));

        tests.Add(("数据集描述包含合成标记", () =>
        {
            var describe = SeriesBuilder.Describe(LoadSsd());
            Assert.Contains(describe, "合成示例", "应标明是合成数据");
            Assert.Contains(describe, "240 个采样点", "采样点统计");
        }));

        tests.Add(("图例平均：按 X 区间加权（避免被极短窗口拉高）", () =>
        {
            var even = new ChartSeries("读", SeriesRole.Read, new List<ChartPoint>
            {
                new(0, 100), new(10, 200), new(20, 300),
            });
            Assert.Approx(200, even.MeanY, 1e-9, "等区间时加权平均=简单平均");

            var uneven = new ChartSeries("读", SeriesRole.Read, new List<ChartPoint>
            {
                new(0, 100), new(1, 100), new(100, 1000),
            });
            // 梯形加权：第一段 1 秒均值 100；第二段 99 秒均值 (100+1000)/2 = 550
            Assert.Approx((100 * 1 + 550 * 99) / 100d, uneven.MeanY, 1e-6, "长时间段权重大");
        }));
    }

    private static ChartDataSet LoadSsd() =>
        FioLogDataSetLoader.FromFiles(
            "SSD 混合读写（4K）",
            "合成示例数据（非实测）",
            TestData.DemoLog("ssd-mixed_bw.1.log"),
            TestData.DemoLog("ssd-mixed_iops.1.log"),
            isSynthetic: true);
}
