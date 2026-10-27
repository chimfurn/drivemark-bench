using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DriveMark.Core.Options;

/// <summary>校验级别。</summary>
public enum ValidationSeverity
{
    Error,
    Warning,
    Info,
}

/// <summary>一条校验结果。</summary>
public sealed record ValidationIssue(ValidationSeverity Severity, string? OptionName, string Message)
{
    public override string ToString() =>
        OptionName is null ? $"[{Severity}] {Message}" : $"[{Severity}] {OptionName}: {Message}";
}

/// <summary>校验结果集合。</summary>
public sealed class ValidationResult
{
    public ValidationResult(IReadOnlyList<ValidationIssue> issues) => Issues = issues;

    public IReadOnlyList<ValidationIssue> Issues { get; }

    public bool HasErrors => Issues.Any(i => i.Severity == ValidationSeverity.Error);
    public bool HasWarnings => Issues.Any(i => i.Severity == ValidationSeverity.Warning);
    public IEnumerable<ValidationIssue> Errors => Issues.Where(i => i.Severity == ValidationSeverity.Error);
    public IEnumerable<ValidationIssue> Warnings => Issues.Where(i => i.Severity == ValidationSeverity.Warning);

    public static readonly ValidationResult Empty = new([]);

    public override string ToString() => string.Join(Environment.NewLine, Issues.Select(i => i.ToString()));
}

/// <summary>
/// 一份可编辑的 fio 作业配置。只保存用户显式设置过的参数（未设置的交给 fio 默认值），
/// 因此生成的 job 文件干净、可复现，也便于 diff。
/// </summary>
public sealed class FioJobConfig
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>job 段名字（job 文件里必须有具名段，只有 [global] 会被 fio 拒绝）。</summary>
    public string JobName { get; set; } = "fio-gui-job";

    /// <summary>已显式设置的参数。</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>已修改参数数量。</summary>
    public int ModifiedCount => _values.Count;

    // ---------------------------------------------------------------- 读写

    /// <summary>设置参数；失败返回错误（不抛异常，便于界面直接展示）。</summary>
    public ValidationIssue? TrySet(string name, string? value)
    {
        var def = FioOptionCatalog.Find(name);
        if (def is null)
            return new ValidationIssue(ValidationSeverity.Error, name, $"未知的 fio 参数：{name}");

        if (string.IsNullOrWhiteSpace(value))
        {
            _values.Remove(def.Name);
            return null;
        }

        var normalized = Normalize(def, value.Trim(), out var issue);
        if (issue is not null)
            return issue;

        _values[def.Name] = normalized!;
        return null;
    }

    /// <summary>批量设置；返回所有失败项。</summary>
    public IReadOnlyList<ValidationIssue> TrySetMany(IEnumerable<KeyValuePair<string, string>> values)
    {
        var issues = new List<ValidationIssue>();
        foreach (var kv in values)
        {
            var issue = TrySet(kv.Key, kv.Value);
            if (issue is not null)
                issues.Add(issue);
        }
        return issues;
    }

    public string? Get(string name) =>
        _values.TryGetValue(name, out var v) ? v : null;

    /// <summary>取生效值：显式值优先，否则用 fio 默认值。</summary>
    public string? EffectiveValue(string name)
    {
        if (_values.TryGetValue(name, out var v))
            return v;
        return FioOptionCatalog.Find(name)?.DefaultValue;
    }

    public void Reset(string name)
    {
        if (FioOptionCatalog.Find(name) is { } def)
            _values.Remove(def.Name);
    }

    public void ResetAll() => _values.Clear();

    /// <summary>参数是否被显式设置过（界面用来标记“已修改”）。</summary>
    public bool IsExplicitlySet(string name) => _values.ContainsKey(name);

    // ---------------------------------------------------------------- 归一化与校验

    private static string? Normalize(FioOptionDefinition def, string raw, out ValidationIssue? issue)
    {
        issue = null;

        // 防御性检查：任何参数值都不允许换行/方括号，避免生成出非法的 job 文件。
        if (raw.IndexOfAny(['\r', '\n']) >= 0)
        {
            issue = new ValidationIssue(ValidationSeverity.Error, def.Name, "参数值不允许包含换行符");
            return null;
        }

        switch (def.Type)
        {
            case FioOptionType.Bool:
                if (raw is "1" or "0")
                    return raw;
                if (bool.TryParse(raw, out var b))
                    return b ? "1" : "0";
                if (raw is "yes" or "on")
                    return "1";
                if (raw is "no" or "off")
                    return "0";
                issue = new ValidationIssue(ValidationSeverity.Error, def.Name, $"布尔参数只能取 0 或 1，收到：{raw}");
                return null;

            case FioOptionType.StringSet:
                // fio 里这是“无值开关”，job 文件写 1 即可。
                return "1";

            case FioOptionType.Integer:
            {
                if (!long.TryParse(raw, out var n))
                {
                    issue = new ValidationIssue(ValidationSeverity.Error, def.Name, $"需要整数，收到：{raw}");
                    return null;
                }
                if (def.Min is { } min && n < (long)min)
                {
                    issue = new ValidationIssue(ValidationSeverity.Error, def.Name, $"不能小于 {min}，收到：{n}");
                    return null;
                }
                if (def.Max is { } max && n > (long)max)
                {
                    issue = new ValidationIssue(ValidationSeverity.Error, def.Name, $"不能大于 {max}，收到：{n}");
                    return null;
                }
                return n.ToString();
            }

            case FioOptionType.SizeOrTime:
            {
                if (raw.Any(char.IsWhiteSpace))
                {
                    issue = new ValidationIssue(ValidationSeverity.Error, def.Name,
                        $"尺寸/时间值不能包含空白字符：\"{raw}\"（正确写法如 4096、4k、1g、10s、500ms）");
                    return null;
                }
                if (!IsValidSizeOrTime(raw, out var reason))
                {
                    issue = new ValidationIssue(ValidationSeverity.Error, def.Name, reason);
                    return null;
                }
                return raw;
            }

            case FioOptionType.Enum:
            {
                var match = def.Choices?
                    .FirstOrDefault(c => string.Equals(c, raw, StringComparison.OrdinalIgnoreCase));
                if (match is null && def.Choices is { Count: > 0 })
                {
                    issue = new ValidationIssue(ValidationSeverity.Error, def.Name,
                        $"只能取 {string.Join("/", def.Choices)}，收到：{raw}");
                    return null;
                }
                return match ?? raw;
            }

            case FioOptionType.String:
            default:
                if (raw.IndexOfAny(['[', ']']) >= 0)
                {
                    issue = new ValidationIssue(ValidationSeverity.Error, def.Name, "参数值不允许包含 [ 或 ]");
                    return null;
                }
                return raw;
        }
    }

    private static bool IsValidSizeOrTime(string raw, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            reason = "值不能为空";
            return false;
        }

        // 形如 4096 / 4k / 1g / 10s / 500ms / 1MiB / 50%
        int i = 0;
        while (i < raw.Length && (char.IsDigit(raw[i]) || raw[i] == '.'))
            i++;

        if (i == 0)
        {
            reason = $"需要以数字开头，收到：{raw}";
            return false;
        }

        var suffix = raw[i..];
        if (suffix.Length > 4)
        {
            reason = $"单位后缀过长：{suffix}";
            return false;
        }

        const string allowed = "kmgtpeKMGTPEisubmhdSUBMHD%";
        foreach (var ch in suffix)
        {
            if (allowed.IndexOf(ch) < 0)
            {
                reason = $"不支持的尺寸/时间单位字符 '{ch}'（可用 k/m/g/t、KiB/MiB、s/ms 等）";
                return false;
            }
        }
        return true;
    }

    /// <summary>静态校验（不依赖运行 fio）。</summary>
    public ValidationResult Validate()
    {
        var issues = new List<ValidationIssue>();

        // 1) 逐参数静态检查（防止有人绕过 TrySet 直接改字典）。
        foreach (var (name, raw) in _values)
        {
            var def = FioOptionCatalog.Find(name);
            if (def is null)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, name, "未知参数"));
                continue;
            }
            var normalized = Normalize(def, raw, out var issue);
            if (issue is not null)
                issues.Add(issue);
            else if (!string.Equals(normalized, raw, StringComparison.Ordinal))
                issues.Add(new ValidationIssue(ValidationSeverity.Warning, name, $"值将被归一化为 {normalized}"));
        }

        // 2) job 名合法性：job 段名不能含空格/方括号。
        if (string.IsNullOrWhiteSpace(JobName))
            issues.Add(new ValidationIssue(ValidationSeverity.Error, null, "job 名不能为空"));
        else if (JobName.Any(c => char.IsWhiteSpace(c) || c is '[' or ']' or ',' or ';'))
            issues.Add(new ValidationIssue(ValidationSeverity.Error, null,
                "job 名不能包含空格、方括号、逗号或分号"));

        // 3) 写入类风险提示（只提示，绝不自动阻止用户，但必须显眼）。
        var rw = EffectiveValue("rw") ?? "read";
        bool writesData = rw.Contains("write", StringComparison.OrdinalIgnoreCase) || rw is "rw" or "randrw" or "trimwrite";
        if (writesData && (Get("readonly") is null || Get("readonly") == "0"))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "rw",
                $"当前 rw={rw} 会真实写入磁盘；若只是验证流程，建议开启只读安全模式（fio 命令行 --readonly）。"));
        }
        if (Get("unlink") == "1")
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "unlink",
                "unlink=1 会删除测试文件，且实测 Windows 上 fio 的删除并不可靠，建议由程序自行清理。"));
        if (Get("overwrite") == "1")
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "overwrite",
                "overwrite=1 会覆盖目标文件既有内容，请确认目标文件可被覆盖。"));
        if (EffectiveValue("direct") == "0")
            issues.Add(new ValidationIssue(ValidationSeverity.Info, "direct",
                "direct=0 会走系统缓存，测出的速度可能明显高于真实盘速。"));
        if (EffectiveValue("thread") is null)
            issues.Add(new ValidationIssue(ValidationSeverity.Info, "thread",
                "建议显式设置 thread=1：实测可消除 Windows 上的 mutex 告警，并让 JSON 输出不再带前置脏文本。"));

        // 4) 图表数据链路检查：曲线要有数据，必须开日志。
        bool hasBwLog = !string.IsNullOrEmpty(Get("write_bw_log"));
        bool hasIopsLog = !string.IsNullOrEmpty(Get("write_iops_log"));
        if (!hasBwLog || !hasIopsLog)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Info, null,
                "未同时开启 write_bw_log 与 write_iops_log：图表的“吞吐量/IOPS”曲线将缺少对应数据源。" +
                "（吞吐量依赖 bw 日志，IOPS 依赖 iops 日志）"));
        }

        // 5) 采样窗口：fio 源码 init.c:1695 使用 min(log_avg_msec, bwavgtime) 作为实际窗口。
        if (hasBwLog)
        {
            var logAvg = ParseLong(Get("log_avg_msec"));
            var bwAvg = ParseLong(Get("bwavgtime")) ?? 500;
            if (logAvg is 0)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Warning, "log_avg_msec",
                    "log_avg_msec=0 表示每个 IO 都记一行日志，长时间测试会产生巨量日志文件；做曲线建议设为 1000。"));
            }
            else if (logAvg is { } la && la > 0 && bwAvg != la)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Info, "bwavgtime",
                    $"实际带宽采样窗口 = min(log_avg_msec={la}, bwavgtime={bwAvg}) = {Math.Min(la, bwAvg)} ms" +
                    (Math.Min(la, bwAvg) != la ? "；想看 1 秒粒度需把 bwavgtime 也设为 1000。" : "。")));
            }
        }

        // 6) time_based / runtime 组合。
        bool timeBased = Get("time_based") == "1";
        var runtime = Get("runtime");
        if (timeBased && string.IsNullOrEmpty(runtime))
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "runtime",
                "time_based=1 但没有设置 runtime，任务可能一直跑下去。"));
        if (!timeBased && !string.IsNullOrEmpty(runtime))
            issues.Add(new ValidationIssue(ValidationSeverity.Info, "time_based",
                "设置了 runtime 但未开 time_based：数据量跑完就会提前结束，未必跑满指定时长。"));

        // 7) 引擎与队列深度：同步引擎忽略 iodepth。
        var engine = EffectiveValue("ioengine") ?? "windowsaio";
        var iodepth = ParseLong(Get("iodepth")) ?? 1;
        if (engine is "sync" or "psync" or "vsync" && iodepth > 1)
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "iodepth",
                $"ioengine={engine} 是同步引擎，会忽略 iodepth={iodepth} 的并发设置。"));

        // 8) 路径安全：fio 在 Windows 上打不开非 ASCII 路径（本工作区已实测 err=2 filesetup.c）。
        //    另外：为了让程序"只动自己创建的文件"，禁止手填绝对路径/盘符根目录（测试数据由执行器自动管理）。
        foreach (var pathOption in new[] { "filename", "directory" })
        {
            var p = Get(pathOption);
            if (string.IsNullOrEmpty(p))
                continue;

            if (p.Any(c => c > 127))
                issues.Add(new ValidationIssue(ValidationSeverity.Warning, pathOption,
                    "路径包含非 ASCII 字符（如中文）。实测 fio 在 Windows 上会报 " +
                    "err=2/file:filesetup.c open \"No such file or directory\"，建议改用纯 ASCII 目录。"));

            if (Path.IsPathRooted(p) || p.Contains(':'))
                issues.Add(new ValidationIssue(ValidationSeverity.Error, pathOption,
                    "为安全起见禁止手动指定绝对路径/盘符（避免误操作其它位置的数据）。" +
                    "测试文件路径由程序自动管理：<目标盘>\\DriveMark-test\\<运行号>\\。"));
        }

        // 9) direct=1 的块大小对齐。
        if (EffectiveValue("direct") == "1")
        {
            var bs = Get("bs");
            if (!string.IsNullOrEmpty(bs) && TryParseSiBytes(bs) is { } bsBytes && bsBytes % 512 != 0)
                issues.Add(new ValidationIssue(ValidationSeverity.Warning, "bs",
                    $"direct=1 时块大小需按扇区对齐（{bsBytes} 不是 512 的整数倍）。"));
        }

        // 10) job 文件里的 filename 不能带盘符冒号（实测坑位）。
        var filenameValue = Get("filename");
        if (!string.IsNullOrEmpty(filenameValue) && filenameValue.Contains(':'))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "filename",
                "job 文件里的 filename 含冒号。fio 把 ':' 当作多文件分隔符，会把 Windows 盘符拆开" +
                "（实测会生成名为 \"E\" 的垃圾文件并报 filesetup.c open ENOENT）。" +
                "建议只写相对文件名，并让 fio 的工作目录指向目标盘。"));
        }

        return new ValidationResult(issues);
    }

    private static long? ParseLong(string? s) =>
        long.TryParse(s, out var v) ? v : null;

    private static long? TryParseSiBytes(string s)
    {
        int i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.'))
            i++;
        if (i == 0 || !double.TryParse(s[..i], out var number))
            return null;
        var suffix = s[i..].ToLowerInvariant();
        double mult = suffix switch
        {
            "" or "b" => 1,
            "k" or "kb" or "kib" => 1024,
            "m" or "mb" or "mib" => 1024d * 1024,
            "g" or "gb" or "gib" => 1024d * 1024 * 1024,
            "t" or "tb" or "tib" => 1024d * 1024 * 1024 * 1024,
            _ => 0,
        };
        return mult <= 0 ? null : (long)(number * mult);
    }

    // ---------------------------------------------------------------- 生成 job 文件 / 命令行

    /// <summary>生成 fio job 文件文本。只包含显式设置的参数。</summary>
    public string BuildJobFile(string? headerComment = null)
    {
        var sb = new StringBuilder();
        sb.Append("# 由 DriveMark 生成 ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).AppendLine();
        if (!string.IsNullOrWhiteSpace(headerComment))
            sb.Append("# ").Append(headerComment.ReplaceLineEndings(" ")).AppendLine();
        sb.AppendLine("# 注意：job 文件必须包含具名 job 段，只有 [global] 会被 fio 拒绝（No job(s) defined）。");
        sb.AppendLine();

        sb.AppendLine("[global]");
        foreach (var def in FioOptionCatalog.JobFileOptions)
        {
            var value = Get(def.Name);
            if (string.IsNullOrEmpty(value))
                continue;
            sb.Append(def.Name).Append('=').AppendLine(value);
        }

        sb.AppendLine();
        sb.Append('[').Append(JobName).AppendLine("]");
        return sb.ToString();
    }

    /// <summary>生成 fio 命令行参数（不含 exe 路径）。</summary>
    public IReadOnlyList<string> BuildCommandLineArguments(string? jobFilePath = null)
    {
        var args = new List<string>();
        foreach (var def in FioOptionCatalog.All.Where(o => o.BelongsToCommandLine))
        {
            if (!_values.TryGetValue(def.Name, out var raw) || string.IsNullOrWhiteSpace(raw))
                continue;
            if (def.Type == FioOptionType.Bool)
            {
                if (raw == "1")
                    args.Add("--" + def.CliName);
                continue;
            }
            args.Add($"--{def.CliName}={raw}");
        }
        if (!string.IsNullOrWhiteSpace(jobFilePath))
            args.Add(jobFilePath);
        return args;
    }

    // ---------------------------------------------------------------- 序列化

    public FioJobConfig Clone()
    {
        var clone = new FioJobConfig { JobName = JobName };
        foreach (var (k, v) in _values)
            clone._values[k] = v;
        return clone;
    }

    public FioJobConfig ApplyPreset(FioPreset preset)
    {
        var next = new FioJobConfig { JobName = JobName };
        foreach (var (k, v) in preset.Values)
        {
            var issue = next.TrySet(k, v);
            if (issue is not null)
                throw new InvalidOperationException($"预设 {preset.Name} 含非法参数：{issue}");
        }
        return next;
    }

    private sealed record ConfigDto(
        [property: JsonPropertyName("jobName")] string JobName,
        [property: JsonPropertyName("values")] Dictionary<string, string> Values);

    public string ToJson()
    {
        var dto = new ConfigDto(JobName, new Dictionary<string, string>(_values, StringComparer.OrdinalIgnoreCase));
        return JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
    }

    public static FioJobConfig FromJson(string json)
    {
        var dto = JsonSerializer.Deserialize<ConfigDto>(json,
                      new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                  ?? throw new InvalidOperationException("配置文件为空或格式错误");
        var config = new FioJobConfig { JobName = dto.JobName };
        foreach (var (k, v) in dto.Values)
        {
            var issue = config.TrySet(k, v);
            if (issue is not null)
                throw new InvalidOperationException($"配置文件含非法参数：{issue}");
        }
        return config;
    }
}
