namespace DriveMark.Core;

/// <summary>界面语言。</summary>
public enum AppLanguage
{
    Chinese = 0,
    English = 1,
}

/// <summary>
/// 共享界面文案的语言上下文（图表轴标题、序列名、空数据提示等）。
/// App 切换语言时设置 <see cref="Current"/>，Core/Chart 中需要随语言变化的文案通过 <see cref="T"/> 取。
/// </summary>
public static class AppText
{
    public static AppLanguage Current { get; set; } = AppLanguage.Chinese;

    public static bool IsEnglish => Current == AppLanguage.English;

    public static string T(string chinese, string english) => IsEnglish ? english : chinese;
}
