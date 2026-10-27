using System.IO;
using DriveMark.Core.Parsing;

namespace DriveMark.Tests;

/// <summary>fio 日志 / 实时进度行 / JSON 结果解析的测试。</summary>
internal static class TestsParsers
{
    public static void Register(List<(string Name, Action Body)> tests)
    {
        // ---------------- 文本日志（格式来自 fio-3.37 iolog.c flush_samples）
        tests.Add(("日志解析：标准 5 列", () =>
        {
            var ok = FioLogParser.TryParseLine("1000, 379512, 0, 4096, 0", out var sample);
            Assert.True(ok, "标准行应解析成功");
            Assert.Equal(1000L, sample.TimeMs, "时间(ms)");
            Assert.Approx(379512, sample.Value, 1e-9, "数值");
            Assert.Equal(0, sample.Direction, "方向 read");
            Assert.Equal(4096L, sample.BlockSize, "块大小");
            Assert.Equal("read", sample.DirectionName, "方向名");
        }));

        tests.Add(("日志解析：写入方向与小数", () =>
        {
            var ok = FioLogParser.TryParseLine("2000, 120.5, 1, 4096, 0", out var sample);
            Assert.True(ok, "应解析成功");
            Assert.True(sample.IsWrite, "方向应为写");
            Assert.Approx(120.5, sample.Value, 1e-9, "小数数值");
        }));

        tests.Add(("日志解析：带偏移列（log_offset）", () =>
        {
            var ok = FioLogParser.TryParseLine("1000, 379512, 0, 4096, 12345678, 0", out var sample);
            Assert.True(ok, "带偏移列也应解析");
            Assert.Equal(1000L, sample.TimeMs, "时间");
            Assert.Approx(379512, sample.Value, 1e-9, "数值");
            Assert.Equal(4096L, sample.BlockSize, "块大小");
        }));

        tests.Add(("日志解析：非法行被拒绝", () =>
        {
            Assert.False(FioLogParser.TryParseLine("", out _), "空行");
            Assert.False(FioLogParser.TryParseLine(null, out _), "null");
            Assert.False(FioLogParser.TryParseLine("garbage", out _), "垃圾文本");
            Assert.False(FioLogParser.TryParseLine("1000, 200, 0", out _), "字段不足");
            Assert.False(FioLogParser.TryParseLine("abc, 200, 0, 4096", out _), "时间非数字");
            Assert.False(FioLogParser.TryParseLine("1000, xyz, 0, 4096", out _), "数值非数字");
        }));

        tests.Add(("日志类型识别", () =>
        {
            Assert.Equal(FioLogKind.Bandwidth, FioLogParser.KindFromFileName("job_bw.1.log"), "bw 日志");
            Assert.Equal(FioLogKind.Iops, FioLogParser.KindFromFileName("job_iops.1.log"), "iops 日志");
            Assert.Equal(FioLogKind.Latency, FioLogParser.KindFromFileName("job_clat.1.log"), "clat 日志");
            Assert.Equal(FioLogKind.Unknown, FioLogParser.KindFromFileName("random.txt"), "未知");
        }));

        tests.Add(("真实合成日志文件解析（bw）", () =>
        {
            var path = TestData.DemoLog("ssd-mixed_bw.1.log");
            Assert.True(TestData.Has(path), "示例日志应存在");
            var samples = FioLogParser.ParseFile(path);
            Assert.Equal(120, samples.Count, "120 行采样");
            var directions = FioLogParser.DistinctDirections(samples);
            Assert.Equal(2, directions.Count, "读写两个方向");
            Assert.True(samples[0].TimeMs == 1000, "首行时间应为 1000ms");
        }));

        tests.Add(("按方向筛选日志并按时间排序", () =>
        {
            var samples = new List<FioLogSample>
            {
                new(3000, 30, 0, 4096, 0),
                new(1000, 10, 0, 4096, 0),
                new(2000, 20, 0, 4096, 0),
                new(1000, 99, 1, 4096, 0),
            };
            var reads = FioLogParser.FilterDirection(samples, 0);
            Assert.Equal(3, reads.Count, "应有 3 条读采样");
            Assert.Equal(1000L, reads[0].TimeMs, "应按时间升序");
            var writes = FioLogParser.FilterDirection(samples, 1);
            Assert.Equal(1, writes.Count, "应有 1 条写采样");
        }));

        // ---------------- 实时进度行
        tests.Add(("进度行解析：实测样本（读）", () =>
        {
            const string line = "Jobs: 1 (f=2): [r(1)][27.3%][r=379MiB/s][r=97.1k IOPS][eta 00m:08s]";
            var ok = ProgressLineParser.TryParse(line, out var sample);
            Assert.True(ok, "应解析成功");
            Assert.Approx(27.3, sample.Percent!.Value, 1e-9, "百分比");
            Assert.Approx(379, sample.ReadMebibytesPerSecond!.Value, 1e-9, "读带宽 MiB/s");
            Assert.Approx(97100, sample.ReadIops!.Value, 1e-9, "读 IOPS（k 后缀）");
            Assert.Equal(TimeSpan.FromSeconds(8), sample.Eta!.Value, "ETA");
        }));

        tests.Add(("进度行解析：写方向", () =>
        {
            const string line = "job: (f=2): [w(1)][50.0%][w=120MiB/s][w=30.1k IOPS][eta 00m:05s]";
            var ok = ProgressLineParser.TryParse(line, out var sample);
            Assert.True(ok, "应解析成功");
            Assert.Approx(120, sample.WriteMebibytesPerSecond!.Value, 1e-9, "写带宽");
            Assert.Approx(30100, sample.WriteIops!.Value, 1e-9, "写 IOPS");
            Assert.Null(sample.ReadIops, "不应有读 IOPS");
        }));

        tests.Add(("进度行解析：无 k 后缀与 B/s 单位", () =>
        {
            Assert.True(ProgressLineParser.TryParse("[r=500 IOPS]", out var plain), "普通 IOPS 应解析");
            Assert.Approx(500, plain.ReadIops!.Value, 1e-9, "500 IOPS");

            Assert.True(ProgressLineParser.TryParse("[r=1048576B/s]", out var bytes), "B/s 应解析");
            Assert.Approx(1, bytes.ReadMebibytesPerSecond!.Value, 1e-9, "1048576 B/s = 1 MiB/s");
        }));

        tests.Add(("进度行解析：无指标的行被拒绝", () =>
        {
            Assert.False(ProgressLineParser.TryParse("Jobs: 1 (f=2)", out _), "无指标");
            Assert.False(ProgressLineParser.TryParse("", out _), "空行");
        }));

        tests.Add(("真实进度输出文件解析（UTF-16 带 BOM）", () =>
        {
            var path = TestData.BenchSample("live-progress-sample.txt");
            Assert.True(TestData.Has(path), "实测样本应存在");
            var samples = ProgressLineParser.ParseAll(File.ReadAllText(path));
            Assert.True(samples.Count >= 2, $"应至少解析出 2 条进度（实际 {samples.Count}）");
            Assert.True(samples.All(s => s.Percent is > 0 and <= 100), "百分比应在 0~100");
        }));

        // ---------------- JSON 结果
        tests.Add(("JSON 解析：跳过前置脏文本", () =>
        {
            const string raw = "fio: this platform does not support process shared mutexes, forcing use of threads.\n{\"fio version\":\"fio-3.37\",\"jobs\":[]}";
            var stripped = FioJsonParser.StripPreamble(raw);
            Assert.True(stripped.StartsWith('{'), "应裁掉 '{' 之前的文本");

            var result = FioJsonParser.Parse(raw);
            Assert.Equal("fio-3.37", result.FioVersion, "版本");
            Assert.Equal(0, result.Jobs.Count, "无 job");
        }));

        tests.Add(("JSON 解析：读方向字段", () =>
        {
            const string raw = """
                {
                  "fio version": "fio-3.37",
                  "time": "now",
                  "jobs": [{
                    "jobname": "j1", "error": 0,
                    "read": {
                      "bw": 1000, "bw_bytes": 2000000, "iops": 500, "total_ios": 10, "io_bytes": 40960,
                      "slat_ns": {"mean": 100},
                      "clat_ns": {"mean": 200, "percentile": {"99.000000": 1000, "99.900000": 2000}},
                      "lat_ns": {"mean": 300}
                    }
                  }]
                }
                """;
            var result = FioJsonParser.Parse(raw);
            Assert.Equal(1, result.Jobs.Count, "1 个 job");
            var read = result.Jobs[0].Read;
            Assert.NotNull(read, "应解析出读结果");
            Assert.Approx(500, read!.Iops, 1e-9, "IOPS");
            Assert.Approx(2000000 / (1024d * 1024d), read.MebibytesPerSecond, 1e-9, "带宽 MiB/s");
            Assert.Approx(0.3, read.LatencyMeanMicroseconds, 1e-9, "平均延迟 µs");
            Assert.Approx(1000, read.ClatP99Ns, 1e-9, "P99 完成延迟");
            Assert.Null(result.Jobs[0].Write, "没有写数据时应为 null");
        }));

        tests.Add(("JSON 解析：缺少 jobs 不崩", () =>
        {
            var result = FioJsonParser.Parse("{\"fio version\":\"x\"}");
            Assert.Equal(0, result.Jobs.Count, "空 jobs");
        }));

        tests.Add(("JSON 解析：没有 JSON 起始符时抛异常", () =>
        {
            Assert.Throws<InvalidDataException>(
                () => FioJsonParser.Parse("just some text without json"),
                "应抛 InvalidDataException");
        }));

        tests.Add(("真实 fio JSON 样本解析", () =>
        {
            var path = TestData.BenchSample("fio-json-sample.json");
            Assert.True(TestData.Has(path), "实测 JSON 样本应存在");
            var result = FioJsonParser.ParseFile(path);
            Assert.Equal("fio-3.37", result.FioVersion, "fio 版本");
            Assert.Equal(1, result.Jobs.Count, "1 个 job");

            var read = result.Jobs[0].Read;
            Assert.NotNull(read, "应有读结果");
            Assert.True(read!.Iops is > 40_000 and < 80_000, $"IOPS 应落在合理区间（实际 {read.Iops:F0}）");
            Assert.True(read.MebibytesPerSecond is > 150 and < 300,
                $"带宽应落在合理区间（实际 {read.MebibytesPerSecond:F1} MiB/s）");
            Assert.True(read.ClatP99Ns > 0, "P99 延迟应有值");
        }));
    }
}
