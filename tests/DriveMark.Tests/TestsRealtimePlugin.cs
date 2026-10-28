using DriveMark.Core.Parsing;
using DriveMark.Plugins.Realtime;

namespace DriveMark.Tests;

internal static class TestsRealtimePlugin
{
    public static void Register(List<(string Name, Action Body)> tests)
    {
        tests.Add(("实时日志插件：增量解析 live 采样文件", () =>
        {
            var dir = TestData.CreateTempDirectory("realtime-plugin");
            Directory.CreateDirectory(dir);
            try
            {
                var plugin = new RealtimeLogPlugin();
                var received = new List<DriveMark.Core.Execution.FioLogTailSample>();
                var gate = new object();

                plugin.Start(dir, samples =>
                {
                    lock (gate)
                        received.AddRange(samples);
                });

                File.WriteAllText(Path.Combine(dir, "drivemark-live-1_bw.1.log"),
                    "100, 1024, 0, 4096, 0\n200, 2048, 0, 4096, 0\n");

                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < deadline)
                {
                    lock (gate)
                    {
                        if (received.Count > 0)
                            break;
                    }
                    Thread.Sleep(50);
                }

                plugin.Stop();

                lock (gate)
                {
                    Assert.True(received.Count >= 1, "应收到 live 采样");
                    Assert.Equal(FioLogKind.Bandwidth, received[0].Kind, "日志类型");
                    Assert.Equal(1024, received[0].Sample.Value, "采样值");
                }
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }));
    }
}