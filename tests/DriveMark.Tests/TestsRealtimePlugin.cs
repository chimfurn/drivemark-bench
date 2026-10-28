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

        tests.Add(("实时日志插件：忽略 slat/clat，只读 lat", () =>
        {
            var dir = TestData.CreateTempDirectory("realtime-lat-only");
            try
            {
                File.WriteAllText(Path.Combine(dir, "drivemark-live-1_lat.1.log"), "10, 1000, 0, 0, 0\n");
                File.WriteAllText(Path.Combine(dir, "drivemark-live-1_slat.1.log"), "10, 5, 0, 0, 0\n");
                File.WriteAllText(Path.Combine(dir, "drivemark-live-1_clat.1.log"), "10, 995, 0, 0, 0\n");

                var plugin = new RealtimeLogPlugin();
                var received = new List<DriveMark.Core.Execution.FioLogTailSample>();
                var gate = new object();
                plugin.Start(dir, samples =>
                {
                    lock (gate)
                        received.AddRange(samples);
                });

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
                    Assert.Equal(1, received.Count, "只应收到 lat 一条");
                    Assert.Equal(FioLogKind.Latency, received[0].Kind, "应为延迟类型");
                    Assert.Equal(1000, received[0].Sample.Value, "应来自 _lat 而不是 _slat/_clat");
                }
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }));
    }
}