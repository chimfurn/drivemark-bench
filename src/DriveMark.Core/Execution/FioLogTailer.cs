using System.IO;
using System.Text;
using DriveMark.Core.Parsing;

namespace DriveMark.Core.Execution;

/// <summary>一条实时日志采样（日志类型 + 采样内容）。</summary>
public readonly record struct FioLogTailSample(FioLogKind Kind, FioLogSample Sample);

/// <summary>
/// 实时追踪 fio 正在写的日志文件，按行增量解析出采样。
///
/// fio 每完成一个采样窗口就向 bw/iops/lat 日志 flush 一行，所以直接 tail 日志可以拿到
/// **全分辨率**数据（10ms 采样时每秒约 100 条）；而 fio 的进度行最快也只有 250ms 一条，
/// 这正是之前"实时绘图点数与采样点数不一致"的原因。
/// </summary>
public sealed class FioLogTailer
{
    private sealed class FileState
    {
        public long Position;
        public string Pending = string.Empty;
        public FioLogKind Kind;
    }

    private readonly string _directory;
    private readonly Dictionary<string, FileState> _files = new(StringComparer.OrdinalIgnoreCase);

    public FioLogTailer(string directory) => _directory = directory;

    public string Directory => _directory;

    /// <summary>读取自上次调用以来新增的完整日志行。</summary>
    public IReadOnlyList<FioLogTailSample> Poll()
    {
        var result = new List<FioLogTailSample>();
        if (!System.IO.Directory.Exists(_directory))
            return result;

        string[] files;
        try
        {
            files = System.IO.Directory.GetFiles(_directory, "*.log");
        }
        catch
        {
            return result;
        }

        foreach (var path in files)
        {
            var kind = ResolveKind(Path.GetFileName(path));
            if (kind == FioLogKind.Unknown)
                continue;

            if (!_files.TryGetValue(path, out var state))
            {
                state = new FileState { Kind = kind };
                _files[path] = state;
            }

            ReadNewLines(path, state, result);
        }

        return result;
    }

    /// <summary>只保留主延迟日志，忽略 _clat/_slat（否则同一次采样会有多份）。</summary>
    private static FioLogKind ResolveKind(string fileName)
    {
        var name = fileName.ToLowerInvariant();
        if (name.Contains("_clat.") || name.Contains("_slat."))
            return FioLogKind.Unknown;
        return FioLogParser.KindFromFileName(fileName);
    }

    private static void ReadNewLines(string path, FileState state, List<FioLogTailSample> result)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            // 文件被重建/截断时从头读。
            if (stream.Length < state.Position)
            {
                state.Position = 0;
                state.Pending = string.Empty;
            }

            if (stream.Length == state.Position)
                return;

            stream.Seek(state.Position, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            var chunk = reader.ReadToEnd();
            if (chunk.Length == 0)
                return;

            var content = state.Pending + chunk;
            var lines = content.Split('\n');
            var completeLines = lines.Length - 1;
            for (var i = 0; i < completeLines; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (FioLogParser.TryParseLine(line, out var sample))
                    result.Add(new FioLogTailSample(state.Kind, sample));
            }

            state.Pending = lines[^1];
            state.Position = stream.Position;
        }
        catch (IOException)
        {
            // 日志文件可能正被 fio 写/替换，忽略本次读取，下次再试。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }
}
