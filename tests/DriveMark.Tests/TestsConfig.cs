using DriveMark.Core.Options;

namespace DriveMark.Tests;

/// <summary>fio 参数配置、校验、job 生成与预设的测试。</summary>
internal static class TestsConfig
{
    public static void Register(List<(string Name, Action Body)> tests)
    {
        // ---------------- 目录与默认配置
        tests.Add(("参数目录：名称唯一且都有源码出处", () =>
        {
            var names = FioOptionCatalog.All.Select(o => o.Name).ToList();
            Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "参数名不应重复");
            foreach (var def in FioOptionCatalog.All)
            {
                Assert.True(def.Source.Contains(':'), $"{def.Name} 应标注源码行号");
                Assert.True(def.Help.Length > 5, $"{def.Name} 应有帮助文本");
                Assert.True(def.Type != FioOptionType.Enum || def.Choices is { Count: > 0 },
                    $"{def.Name} 是枚举就必须有可选值");
            }
        }));

        tests.Add(("默认配置带有图表日志参数", () =>
        {
            var config = FioPresets.CreateDefault();
            Assert.True(config.ModifiedCount > 10, $"默认配置应有多个参数（实际 {config.ModifiedCount}）");
            Assert.Equal("randread", config.Get("rw"), "默认只读");
            Assert.Equal("fio-gui-bw", config.Get("write_bw_log"), "默认开启 bw 日志");
            Assert.Equal("fio-gui-iops", config.Get("write_iops_log"), "默认开启 iops 日志");
            Assert.False(config.Validate().HasErrors, "默认配置不应有错误");
        }));

        tests.Add(("job 文件：必须包含具名 job 段", () =>
        {
            var job = FioPresets.CreateDefault().BuildJobFile("test");
            Assert.Contains(job, "[global]", "应包含 global 段");
            Assert.Contains(job, "[fio-gui-job]", "必须包含具名 job 段");
            Assert.Contains(job, "rw=randread", "应包含显式参数");
            Assert.Contains(job, "write_bw_log=fio-gui-bw", "应包含日志参数");
            Assert.NotContains(job, "nrfiles=", "未设置的参数不应出现");
            // [global] 必须在具名段之前
            Assert.True(job.IndexOf("[global]", StringComparison.Ordinal) <
                        job.IndexOf("[fio-gui-job]", StringComparison.Ordinal), "global 段应在前");
        }));

        // ---------------- 参数设置与归一化
        tests.Add(("参数设置：非法值被拒绝", () =>
        {
            var config = new FioJobConfig();
            Assert.NotNull(config.TrySet("not_an_option", "1"), "未知参数应报错");
            Assert.NotNull(config.TrySet("iodepth", "0"), "小于最小值应报错");
            Assert.NotNull(config.TrySet("iodepth", "abc"), "非整数应报错");
            Assert.NotNull(config.TrySet("rw", "banana"), "非法枚举应报错");
            Assert.NotNull(config.TrySet("rwmixread", "101"), "超过最大值应报错");
            Assert.NotNull(config.TrySet("bs", "4 k"), "带空格应报错");
            Assert.NotNull(config.TrySet("filename", "a\nb"), "含换行应报错");
            Assert.NotNull(config.TrySet("size", "abc"), "尺寸非法应报错");
        }));

        tests.Add(("参数设置：大小写归一化", () =>
        {
            var config = new FioJobConfig();
            Assert.Null(config.TrySet("rw", "RANDWRITE"), "枚举应大小写不敏感");
            Assert.Equal("randwrite", config.Get("rw"), "应归一化为小写枚举值");
            Assert.Null(config.TrySet("direct", "true"), "布尔可用 true");
            Assert.Equal("1", config.Get("direct"), "true 归一化为 1");
        }));

        tests.Add(("参数设置：生效值回退到 fio 默认值", () =>
        {
            var config = new FioJobConfig();
            Assert.Equal("4096", config.EffectiveValue("bs"), "未设置时取 fio 默认值");
            Assert.Null(config.Get("bs"), "显式值仍为空");
            config.TrySet("bs", "8k");
            Assert.Equal("8k", config.EffectiveValue("bs"), "显式值优先");
            Assert.True(config.IsExplicitlySet("bs"), "应标记为已设置");
            config.Reset("bs");
            Assert.Null(config.Get("bs"), "重置后显式值清空");
            Assert.Equal("4096", config.EffectiveValue("bs"), "重置后回到默认");
        }));

        // ---------------- 校验规则
        tests.Add(("校验：写模式安全提示", () =>
        {
            var config = new FioJobConfig();
            config.TrySet("rw", "randwrite");
            var result = config.Validate();
            Assert.True(result.HasWarnings, "写模式应有警告");
            Assert.True(result.Issues.Any(i => i.Message.Contains("会真实写入磁盘", StringComparison.Ordinal)),
                "应提示会写盘");
        }));

        tests.Add(("校验：只读模式抑制写盘警告", () =>
        {
            var config = new FioJobConfig();
            config.TrySet("rw", "randwrite");
            config.TrySet("readonly", "1");
            var result = config.Validate();
            Assert.False(result.Issues.Any(i => i.Message.Contains("会真实写入磁盘", StringComparison.Ordinal)),
                "开启只读安全模式后不应再提示写盘");
        }));

        tests.Add(("校验：非 ASCII 路径警告（fio 实测限制）", () =>
        {
            var config = new FioJobConfig();
            config.TrySet("filename", @"D:\测试目录\test.dat");
            var result = config.Validate();
            Assert.True(result.Issues.Any(i => i.OptionName == "filename" &&
                                               i.Message.Contains("err=2", StringComparison.Ordinal)),
                "应提示 fio 无法打开非 ASCII 路径");
        }));

        tests.Add(("校验：direct=1 时块大小需按 512 对齐", () =>
        {
            var config = new FioJobConfig();
            config.TrySet("direct", "1");
            config.TrySet("bs", "1000");
            var result = config.Validate();
            Assert.True(result.Issues.Any(i => i.OptionName == "bs" &&
                                               i.Message.Contains("512", StringComparison.Ordinal)),
                "非对齐块大小应警告");
        }));

        tests.Add(("校验：log_avg_msec=0 会记录每个 IO", () =>
        {
            var config = new FioJobConfig();
            config.TrySet("write_bw_log", "x");
            config.TrySet("log_avg_msec", "0");
            var result = config.Validate();
            Assert.True(result.Issues.Any(i => i.OptionName == "log_avg_msec" &&
                                               i.Message.Contains("每个 IO", StringComparison.Ordinal)),
                "应提示会逐 IO 记录，并判为错误");
        }));

        tests.Add(("校验：采样窗口取 min(log_avg_msec, bwavgtime)", () =>
        {
            var config = new FioJobConfig();
            config.TrySet("write_bw_log", "x");
            config.TrySet("log_avg_msec", "1000");
            config.TrySet("bwavgtime", "500");
            var result = config.Validate();
            Assert.True(result.Issues.Any(i => i.Message.Contains("min(log_avg_msec", StringComparison.Ordinal)),
                "应提示实际窗口取两者最小值");
        }));

        tests.Add(("校验：job 名非法", () =>
        {
            var config = new FioJobConfig { JobName = "bad name" };
            Assert.True(config.Validate().HasErrors, "含空格的 job 名应报错");
            config.JobName = "good-name_1";
            Assert.False(config.Validate().HasErrors, "合法 job 名不应报错");
        }));

        tests.Add(("校验：未开日志时提示图表缺少数据源", () =>
        {
            var config = new FioJobConfig();
            var result = config.Validate();
            Assert.True(result.Issues.Any(i => i.Message.Contains("write_bw_log", StringComparison.Ordinal)),
                "应提示需要日志参数");
        }));

        // ---------------- 命令行生成
        tests.Add(("命令行参数：只读开关与输出格式", () =>
        {
            var config = new FioJobConfig();
            config.TrySet("readonly", "1");
            config.TrySet("output-format", "json");
            var args = config.BuildCommandLineArguments("job.fio");
            Assert.True(args.Contains("--readonly"), "应包含 --readonly");
            Assert.True(args.Contains("--output-format=json"), "应包含 --output-format=json");
            Assert.Equal("job.fio", args[^1], "job 文件应作为最后一个参数");
        }));

        tests.Add(("命令行参数：布尔为 0 时不输出", () =>
        {
            var config = new FioJobConfig();
            config.TrySet("readonly", "0");
            var args = config.BuildCommandLineArguments();
            Assert.False(args.Contains("--readonly"), "readonly=0 不应出现在命令行");
        }));

        // ---------------- 序列化
        tests.Add(("配置 JSON 往返", () =>
        {
            var original = FioPresets.CreateDefault();
            original.JobName = "roundtrip-job";
            var json = original.ToJson();
            var loaded = FioJobConfig.FromJson(json);
            Assert.Equal(original.JobName, loaded.JobName, "job 名应保持");
            Assert.Equal(original.ModifiedCount, loaded.ModifiedCount, "参数数量应保持");
            Assert.Equal(original.Get("bs"), loaded.Get("bs"), "参数值应保持");
            Assert.Equal(original.BuildJobFile(), loaded.BuildJobFile(), "生成的 job 文件应一致");
        }));

        // ---------------- 预设
        tests.Add(("预设：全部可应用且无参数错误", () =>
        {
            foreach (var preset in FioPresets.All)
            {
                var config = new FioJobConfig { JobName = "preset-test" }.ApplyPreset(preset);
                Assert.True(config.ModifiedCount > 0, $"预设 {preset.Name} 应有参数");
                var result = config.Validate();
                Assert.False(result.HasErrors, $"预设 {preset.Name} 不应有参数错误：{result}");
            }
        }));

        tests.Add(("预设：图表采集档位带日志与 1 秒采样", () =>
        {
            var chartPresets = FioPresets.All.Where(p => p.Name.StartsWith("图表采集", StringComparison.Ordinal)).ToList();
            Assert.True(chartPresets.Count >= 4, "应至少 4 个图表采集档位");
            foreach (var preset in chartPresets)
            {
                Assert.Equal("fio-gui-bw", preset.Values["write_bw_log"], $"{preset.Name} 应输出 bw 日志");
                Assert.Equal("fio-gui-iops", preset.Values["write_iops_log"], $"{preset.Name} 应输出 iops 日志");
                Assert.Equal("1000", preset.Values["log_avg_msec"], $"{preset.Name} 应采用 1 秒采样");
                Assert.Equal("1000", preset.Values["bwavgtime"], $"{preset.Name} bw 窗口也应为 1 秒");
            }
        }));

        tests.Add(("预设：写盘标记与 rw 一致", () =>
        {
            foreach (var preset in FioPresets.All)
            {
                var rw = preset.Values.GetValueOrDefault("rw", "read");
                // 本项目的预设里只有 read / randread 是纯只读，其余都包含写入。
                var expectsWrite = rw is not ("read" or "randread");
                Assert.Equal(expectsWrite, preset.WritesData, $"预设 {preset.Name} 的写盘标记应与 rw={rw} 一致");
            }
        }));
    }
}
