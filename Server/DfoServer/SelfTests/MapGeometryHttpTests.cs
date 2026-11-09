using System.Diagnostics;
using System.Net;
using DfoServer.GameWorld.Maps;
using DfoServer.Network.Management;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class MapGeometryHttpTests
{
    private static WebApplication App(Func<string, byte[]?> query, MapGeometryHttpSnapshot? snapshot = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        GmWebServer.MapClientGeometry(app, query);
        if (snapshot is not null) GmWebServer.MapClientGeometryBatch(app, () => snapshot);
        return app;
    }
    public static async Task RunCoreAsync()
    {
        int checks = Count;
        byte[] expected = Enumerable.Range(0,32).Select(i => (byte)i).ToArray();
        await using var app = App(path => path == "map/test.map" ? expected : null);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            That((await client.GetByteArrayAsync("/client/displaycompat-world?map=map%2Ftest.map")).SequenceEqual(expected), "公开几何能力返回实际32字节，不需要GM凭据");
            foreach (string invalid in new[] { "", "?map=../escape.map", "?map=map/%00bad", "?map=map/a:b" })
                That((await client.GetAsync("/client/displaycompat-world" + invalid)).StatusCode == HttpStatusCode.BadRequest, "非法几何路径拒绝");
            That((await client.GetAsync("/client/displaycompat-world?map=map/missing.map")).StatusCode == HttpStatusCode.NotFound, "未提供能力的地图不伪造匹配");
        }
        finally { await app.StopAsync(); }
        Console.WriteLine($"PASS common map geometry HTTP {Count - checks}");
    }
    public static async Task RunInteropAsync(string configuration, string pvfPath, string executable, string vectors)
    {
        MapGeometryTests.RunCore();
        var config = MapGeometryConfig.ReadFile(configuration);
        using var pvf = PvfArchive.Open(pvfPath);
        var signatures = MapGeometryCapabilities.Build(pvf,config,Array.Empty<string>());
        await using var app = App(path => signatures.GetValueOrDefault(path),new MapGeometryHttpSnapshot(signatures));
        await app.StartAsync();
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(vectors);
            start.ArgumentList.Add(app.Urls.Single() + "/client/displaycompat-world");
            // 完整宽屏向量包含碰撞方案，客户端须载入本轮同一world才能计算DCG4。
            start.ArgumentList.Add(configuration);
            using var process = Process.Start(start) ?? throw new IOException("C++ geometry test process not started");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Console.Write(await stdout); Console.Error.Write(await stderr);
            That(process.ExitCode == 0, "实际C++ WinHTTP逐图核对C# Kestrel有效几何能力");
        }
        finally { await app.StopAsync(); }
    }
}
