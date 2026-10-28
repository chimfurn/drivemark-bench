using DriveMark.Core.Execution;

namespace DriveMark.Plugins.Realtime;

/// <summary>
/// 实时日志插件：轮询 fio 写出的 live 采样文件，按行增量解析后回传。
/// 插件独立于主程序编译，关闭时完全不启动轮询，也不影响 fio 原有日志。
/// </summary>
public sealed class RealtimeLogPlugin : IRealtimeLogPlugin
{
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private FioLogTailer? _tailer;
    private Action<IReadOnlyList<FioLogTailSample>>? _onSamples;

    public string Name => "fio realtime log stream";

    public void Start(string liveDirectory, Action<IReadOnlyList<FioLogTailSample>> onSamples)
    {
        Stop();
        Directory.CreateDirectory(liveDirectory);
        _tailer = new FioLogTailer(liveDirectory);
        _onSamples = onSamples;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var tailer = _tailer;

        _loop = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var samples = tailer.Poll();
                    if (samples.Count > 0)
                        onSamples(samples);

                    await Task.Delay(100, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常停止。
            }
            catch
            {
                // 插件错误不能影响主测试流程；GUI 会继续使用进度采样。
            }
        });
    }

    public void Stop()
    {
        var cts = _cts;
        var loop = _loop;
        var tailer = _tailer;
        var onSamples = _onSamples;

        _cts = null;
        _loop = null;
        _tailer = null;
        _onSamples = null;

        if (cts is not null)
        {
            try { cts.Cancel(); } catch { /* ignore */ }
            try { loop?.Wait(800); } catch { /* ignore */ }
            cts.Dispose();
        }

        if (tailer is not null)
        {
            try
            {
                var rest = tailer.Poll();
                if (rest.Count > 0)
                    onSamples?.Invoke(rest);
            }
            catch
            {
                // ignore.
            }
        }
    }
}
