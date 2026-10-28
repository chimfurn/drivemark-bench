namespace DriveMark.Core.Execution;

/// <summary>
/// 可选“实时日志插件”接口。Core 只定义协议，具体实现由独立插件项目提供。
/// 插件在 fio 运行期间读取 live 目录里的采样文件，把全分辨率采样回传给 GUI。
/// </summary>
public interface IRealtimeLogPlugin
{
    /// <summary>插件名称（用于诊断/日志）。</summary>
    string Name { get; }

    /// <summary>开始监视 live 目录；<paramref name="onSamples"/> 可能在后台线程被调用。</summary>
    void Start(string liveDirectory, Action<IReadOnlyList<FioLogTailSample>> onSamples);

    /// <summary>停止监视（可重复调用）。</summary>
    void Stop();
}
