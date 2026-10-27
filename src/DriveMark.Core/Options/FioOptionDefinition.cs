namespace DriveMark.Core.Options;

/// <summary>
/// fio 参数的取值类型。类型决定了编辑器控件、校验规则和 job 文件中的书写方式。
/// 对应 fio options.c 中的 FIO_OPT_* 类型。
/// </summary>
public enum FioOptionType
{
    /// <summary>布尔值，job 文件写 0/1（对应 FIO_OPT_BOOL）。</summary>
    Bool,

    /// <summary>整数，有 min/max 约束（对应 FIO_OPT_INT）。</summary>
    Integer,

    /// <summary>自由字符串，如文件名（对应 FIO_OPT_STR_STORE）。</summary>
    String,

    /// <summary>带单位的尺寸或时间，如 4k / 1g / 10s / 500ms（对应 FIO_OPT_STR_VAL / FIO_OPT_STR_VAL_TIME）。</summary>
    SizeOrTime,

    /// <summary>枚举，只能取 <see cref="FioOptionDefinition.Choices"/> 中的值（对应 FIO_OPT_STR + posval）。</summary>
    Enum,

    /// <summary>无值开关，job 文件写 1（对应 FIO_OPT_STR_SET），如 time_based / norandommap。</summary>
    StringSet,
}

/// <summary>参数作用域：写进 job 文件，还是作为 fio 命令行选项，或两者皆可。</summary>
public enum FioOptionScope
{
    /// <summary>只能写在 job 文件里。</summary>
    JobFile,

    /// <summary>只能作为 fio 命令行选项（如 --output-format）。</summary>
    CommandLine,

    /// <summary>job 文件与命令行都可以。</summary>
    Both,
}

/// <summary>风险等级，用于在界面上给出安全提示。绝不自动执行写入。</summary>
public enum FioRiskLevel
{
    /// <summary>只读或纯统计，无数据风险。</summary>
    Safe,

    /// <summary>会向目标写入数据（可能覆盖或生成大文件）。</summary>
    WritesData,

    /// <summary>可能删除/破坏既有数据（如 unlink、裸盘写入）。</summary>
    Destructive,
}

/// <summary>
/// 一个 fio 参数的可编辑元数据。字段含义、默认值、取值范围均取自本工作区内的
/// fio-3.37 源码（见 <see cref="Source"/>），而不是凭记忆编写。
/// </summary>
public sealed record FioOptionDefinition
{
    /// <summary>fio 精确参数名（job 文件中的键名；命令行选项见 <see cref="CommandLineName"/>）。</summary>
    public required string Name { get; init; }

    /// <summary>中文显示名。</summary>
    public required string DisplayName { get; init; }

    /// <summary>取值类型。</summary>
    public required FioOptionType Type { get; init; }

    /// <summary>作用域。</summary>
    public FioOptionScope Scope { get; init; } = FioOptionScope.JobFile;

    /// <summary>fio 默认值的字符串形式；为 null 表示 fio 侧无默认值（可不填）。</summary>
    public string? DefaultValue { get; init; }

    /// <summary>允许的最小值（仅 Integer 使用）。</summary>
    public double? Min { get; init; }

    /// <summary>允许的最大值（仅 Integer 使用）。</summary>
    public double? Max { get; init; }

    /// <summary>单位提示，如 "ms"、"s"。</summary>
    public string? Unit { get; init; }

    /// <summary>枚举可选值（仅 Enum 使用）。</summary>
    public IReadOnlyList<string>? Choices { get; init; }

    /// <summary>中文帮助文本，显示为界面提示。</summary>
    public required string Help { get; init; }

    /// <summary>分组名，用于界面分栏。</summary>
    public required string Category { get; init; }

    /// <summary>风险等级。</summary>
    public FioRiskLevel Risk { get; init; } = FioRiskLevel.Safe;

    /// <summary>源码出处，便于核对，如 "options.c:1946" 或 "init.c:121"。</summary>
    public required string Source { get; init; }

    /// <summary>若与 job 文件键名不同，这里是命令行使用的名字（带连字符，不含 --）。</summary>
    public string? CommandLineName { get; init; }

    /// <summary>命令行参数名（默认与 Name 相同）。</summary>
    public string CliName => CommandLineName ?? Name;

    /// <summary>该参数是否写入 job 文件。</summary>
    public bool BelongsToJobFile => Scope is FioOptionScope.JobFile or FioOptionScope.Both;

    /// <summary>该参数是否是 fio 命令行选项。</summary>
    public bool BelongsToCommandLine => Scope is FioOptionScope.CommandLine or FioOptionScope.Both;
}
