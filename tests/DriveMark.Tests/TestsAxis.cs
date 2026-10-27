using DriveMark.Core.Charts;

namespace DriveMark.Tests;

/// <summary>坐标轴刻度算法、范围解析与单位格式化的测试。</summary>
internal static class TestsAxis
{
    public static void Register(List<(string Name, Action Body)> tests)
    {
        // ---------------- NiceStep
        tests.Add(("NiceStep 0.37 → 0.5", () =>
            Assert.Approx(0.5, NiceTicks.NiceStep(0.37), 1e-12, "0.37 应向上取到 0.5")));
        tests.Add(("NiceStep 1.2 → 2", () =>
            Assert.Approx(2, NiceTicks.NiceStep(1.2), 1e-12, "1.2 应向上取到 2")));
        tests.Add(("NiceStep 2.4 → 2.5", () =>
            Assert.Approx(2.5, NiceTicks.NiceStep(2.4), 1e-12, "2.4 应向上取到 2.5")));
        tests.Add(("NiceStep 7 → 10", () =>
            Assert.Approx(10, NiceTicks.NiceStep(7), 1e-12, "7 应向上取到 10")));
        tests.Add(("NiceStep 23 → 25", () =>
            Assert.Approx(25, NiceTicks.NiceStep(23), 1e-12, "23 应向上取到 25")));
        tests.Add(("NiceStep 非法输入 → 1", () =>
        {
            Assert.Approx(1, NiceTicks.NiceStep(0), 1e-12, "0 应回退为 1");
            Assert.Approx(1, NiceTicks.NiceStep(-5), 1e-12, "负数应回退为 1");
            Assert.Approx(1, NiceTicks.NiceStep(double.NaN), 1e-12, "NaN 应回退为 1");
        }));

        // ---------------- 自动范围
        tests.Add(("Auto 0~100 目标 6 刻度 → 步长 20", () =>
        {
            var range = AxisResolver.Resolve(new AxisSpec { TargetTickCount = 6 }, [0d, 100d]);
            Assert.Approx(0, range.Min, 1e-9, "最小值");
            Assert.Approx(100, range.Max, 1e-9, "最大值");
            Assert.Approx(20, range.Step, 1e-9, "步长");
            Assert.Equal(6, range.Ticks.Count, "刻度数量");
        }));

        tests.Add(("StartAtZero 强制从 0 起", () =>
        {
            var range = AxisResolver.Resolve(AxisSpec.AutoFromZero(6), [50d, 90d]);
            Assert.Approx(0, range.Min, 1e-9, "应从 0 开始");
            Assert.Approx(100, range.Max, 1e-9, "上界应对齐到 100");
        }));

        tests.Add(("数据全等时自动撑开范围", () =>
        {
            var range = AxisResolver.Resolve(new AxisSpec(), [100d, 100d, 100d]);
            Assert.True(range.Max > range.Min, "范围必须有效");
            Assert.Approx(90, range.Min, 1e-9, "下界应为 90");
            Assert.Approx(110, range.Max, 1e-9, "上界应为 110");
        }));

        tests.Add(("负数据范围", () =>
        {
            var range = AxisResolver.Resolve(new AxisSpec(), [-50d, -10d]);
            Assert.Approx(-50, range.Min, 1e-9, "最小值");
            Assert.Approx(-10, range.Max, 1e-9, "最大值");
        }));

        tests.Add(("空数据返回占位范围", () =>
        {
            var range = AxisResolver.Resolve(new AxisSpec(), []);
            Assert.True(range.Max > range.Min, "空数据的范围必须有效");
            Assert.True(range.Ticks.Count >= 2, "空数据也应有刻度");
        }));

        tests.Add(("NaN/Infinity 被过滤", () =>
        {
            var range = AxisResolver.Resolve(new AxisSpec(), [double.NaN, double.PositiveInfinity, 10d, 20d]);
            Assert.Approx(20, range.Max, 1e-9, "上界来自有效数据");
        }));

        // ---------------- 自定义范围
        tests.Add(("自定义 最小/最大/步长 被精确采用", () =>
        {
            var range = AxisResolver.Resolve(AxisSpec.Manual(10, 50, 5), [0d, 60d]);
            Assert.Approx(10, range.Min, 1e-9, "最小值应被采用");
            Assert.Approx(50, range.Max, 1e-9, "最大值应被采用");
            Assert.Approx(5, range.Step, 1e-9, "步长应被采用");
            Assert.Equal(9, range.Ticks.Count, "10..50 步长 5 应有 9 个刻度");
            Assert.Approx(50, range.Ticks[^1], 1e-9, "最后一个刻度应为 50");
        }));

        tests.Add(("只自定义步长时边界对齐到刻度格点", () =>
        {
            var range = AxisResolver.Resolve(new AxisSpec { ManualStep = 25 }, [0d, 100d]);
            Assert.Approx(25, range.Step, 1e-9, "步长应为 25");
            Assert.Approx(0, range.Min, 1e-9, "下界对齐");
            Assert.Approx(100, range.Max, 1e-9, "上界对齐");
            Assert.Equal(5, range.Ticks.Count, "0..100 步长 25 应有 5 个刻度");
        }));

        tests.Add(("自定义最大 ≤ 最小 时仍产出有效范围", () =>
        {
            var range = AxisResolver.Resolve(AxisSpec.Manual(5, 5), [0d, 10d]);
            Assert.True(range.Max > range.Min, "必须自动修正为有效范围");
        }));

        tests.Add(("Normalize 归一化与裁剪", () =>
        {
            var range = new AxisRange(0, 100, 20, [0, 20, 40, 60, 80, 100]);
            Assert.Approx(0.5, range.Normalize(50), 1e-9, "中点");
            Assert.Approx(0, range.Normalize(-10), 1e-9, "低于下界被裁剪");
            Assert.Approx(1, range.Normalize(200), 1e-9, "高于上界被裁剪");
        }));

        // ---------------- 单位
        tests.Add(("Units.IOPS 轴格式", () =>
        {
            // ≤ 10000：具体整数，不用 k
            Assert.Equal("8000", Units.IopsAxisFor(8000)(8000), "8000 应显示为 8000");
            Assert.Equal("0", Units.IopsAxisFor(8000)(0), "0 显示为 0");
            // > 10000：k 缩写；0 仍为 0
            Assert.Equal("60k", Units.IopsAxisFor(60000)(60000), "60000 应显示为 60k");
            Assert.Equal("10k", Units.IopsAxisFor(60000)(10000), "10000 在 k 刻度下显示 10k");
            Assert.Equal("0", Units.IopsAxisFor(60000)(0), "k 刻度下 0 仍显示 0");
            // 吞吐量：始终具体值，不用 k
            Assert.Equal("1000", Units.ThroughputAxis(1000), "1000 应显示 1000");
            Assert.Equal("1500", Units.ThroughputAxis(1500), "1500 应显示 1500");
            Assert.Equal("0.5", Units.ThroughputAxis(0.5), "小数保持小数");
            // IOPS 汇总值 ≤ 10000 用具体整数
            Assert.Equal("8000", Units.Iops(8000), "8000 不用 8k");
        }));
        tests.Add(("Units.IOPS 缩写", () =>
        {
            Assert.Equal("97.1k", Units.Iops(97142), "97142 → 97.1k");
            Assert.Equal("500", Units.Iops(500), "500 保持整数");
            Assert.Equal("1.2M", Units.Iops(1_200_000), "120 万 → 1.2M");
        }));

        tests.Add(("Units 吞吐量格式", () =>
        {
            Assert.Equal("353 MiB/s", Units.MebibytesPerSecond(353.3), "大数值取整");
            Assert.Equal("55.6 MiB/s", Units.MebibytesPerSecond(55.55), "小数值保留一位");
            Assert.Equal("2 GiB/s", Units.MebibytesPerSecond(2048), "超过 1024 升 GiB");
        }));

        tests.Add(("Units 时间轴始终用秒", () =>
        {
            Assert.Equal("60", Units.SecondsAxis(60), "60 秒不应变成 1m");
            Assert.Equal("10", Units.SecondsAxis(10), "整数秒");
        }));

        tests.Add(("Units 字节/容量", () =>
        {
            Assert.Equal("1 GiB", Units.Bytes(1073741824), "1 GiB");
            Assert.Equal("512 MiB", Units.Mebibytes(512), "512 MiB");
            Assert.Equal("2 GiB", Units.Mebibytes(2048), "2048 MiB → 2 GiB");
        }));

        tests.Add(("Units 时长与百分比", () =>
        {
            Assert.Equal("1:05", Units.Duration(TimeSpan.FromSeconds(65)), "65 秒 → 1:05");
            Assert.Equal("27.3%", Units.Percent(27.3), "百分比格式");
        }));
    }
}
