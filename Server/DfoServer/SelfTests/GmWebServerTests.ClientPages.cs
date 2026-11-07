using System.Net;
using System.Net.Sockets;
using DfoServer.Network.Management;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class GmWebServerTests
{
    public static async Task RunClientPagesAsync()
    {
        await RunManagementBoundaryAsync();
        await ClientPagesAsync(new FakeData(
            Path.Combine(Path.GetTempPath(), "gm-client-web-" + Guid.NewGuid().ToString("N") + ".jsonl")));
    }

    static async Task ClientPagesAsync(FakeData data)
    {
        That(new ServerOptions().RechargeWebPort == 23104, "客户端网页默认使用HTTP23104");
        int clientPort = FreeWebPort();
        using (var stop = new CancellationTokenSource())
        {
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var host = GmWebServer.RunWithRechargeAsync(0, clientPort, data, stop.Token,
                options: new ServerOptions { ListenAddress = "127.0.0.2" }, started: ready.SetResult);
            try
            {
                await AwaitWebStart(host, ready.Task);
                That(await ready.Task, "网页按指定监听地址启动，不依赖GM端口");
                using var http = WebClient("127.0.0.2", clientPort);
                using var page = await http.GetAsync("/client/includeImg.htm");
                string html = await page.Content.ReadAsStringAsync();
                That(page.StatusCode == HttpStatusCode.OK && page.Content.Headers.ContentType?.MediaType == "text/html" &&
                    page.Content.Headers.ContentType.CharSet == "utf-8" &&
                    html.Contains("url(includeImg_bg.png)") && html.Contains("src=\"banner_top.jpg\""),
                    "原生includeImg.htm直接返回内嵌HTML，同级引用两张图片，不跳转到充值页");
                That(!html.Contains("dnf.qq.com") && !html.Contains(".forEach("),
                    "宣传页不固定域名，脚本兼容客户端旧浏览器");
                foreach (var asset in new[] { ("includeImg_bg.png", "image/png"), ("banner_top.jpg", "image/jpeg") })
                {
                    using var response = await http.GetAsync("/client/" + asset.Item1);
                    byte[] actual = await response.Content.ReadAsByteArrayAsync();
                    using var resource = typeof(GmWebServer).Assembly.GetManifestResourceStream("DfoServer.GmWeb.client." + asset.Item1)!;
                    using var expected = new MemoryStream();
                    await resource.CopyToAsync(expected);
                    That(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == asset.Item2 &&
                        actual.Length > 1000 && actual.SequenceEqual(expected.ToArray()), "内嵌图片逐字节匹配发布资源：" + asset.Item1);
                }
                foreach (string path in new[] { "/client/index.html", "/client/missing.png" })
                    That((await http.GetAsync(path)).StatusCode == HttpStatusCode.NotFound, "宣传页不开放任意文件读取：" + path);
                using var foreign = new HttpRequestMessage(HttpMethod.Get, "/client/includeImg.htm");
                foreign.Headers.Host = "dnf.qq.com";
                That((await http.SendAsync(foreign)).StatusCode == HttpStatusCode.Forbidden, "宣传页不绑定或放行旧域名");
                using var api = await http.GetAsync("/api/status");
                That(api.StatusCode == HttpStatusCode.NotFound, "未配置GM入口时不开放管理接口");
            }
            finally { stop.Cancel(); await host; }
        }
        using var occupied = new TcpListener(IPAddress.Any, 0);
        occupied.ExclusiveAddressUse = true;
        occupied.Start();
        int busyPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
        foreach (int gmPort in new[] { 0, FreeWebPort(), busyPort })
        {
            using var stop = new CancellationTokenSource();
            int warnings = 0;
            string address = gmPort != 0 && gmPort != busyPort ? "127.0.0.2" : "127.0.0.1";
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var host = GmWebServer.RunWithRechargeAsync(gmPort, busyPort, data, stop.Token,
                options: new ServerOptions { ListenAddress = address }, started: ready.SetResult, unavailable: _ => warnings++);
            try
            {
                await AwaitWebStart(host, ready.Task);
                That(!await ready.Task && warnings == 1 && !host.IsCompleted,
                    $"客户端HTTP端口占用后仅告警，生命周期保持运行至取消：GM={gmPort} IP={address} available={ready.Task.Result} warnings={warnings} host={host.Status}");
                if (gmPort != 0 && gmPort != busyPort)
                {
                    using var http = WebClient(address, gmPort);
                    That((await http.GetAsync("/api/status")).StatusCode == HttpStatusCode.OK &&
                        (await http.GetAsync("/client/includeImg.htm")).StatusCode == HttpStatusCode.OK,
                        "客户端端口失败后独立GM端口仍可用，复用同一网页路由");
                }
            }
            finally { stop.Cancel(); await host; }
        }
        int freeClientPort = FreeWebPort();
        using (var stop = new CancellationTokenSource())
        {
            bool rejected = false;
            var host = GmWebServer.RunWithRechargeAsync(busyPort, freeClientPort, data, stop.Token);
            try { await host.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception error) when (error is IOException or SocketException) { rejected = true; }
            finally
            {
                stop.Cancel();
                try { await host; }
                catch (Exception error) when (error is IOException or SocketException) { }
            }
            That(rejected, "独立GM端口绑定失败仍明确失败，不误认为仅客户端端口不可用");
        }
        using (var stop = new CancellationTokenSource())
        {
            int sharedPort = FreeWebPort();
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var capture = new Network.PacketCapture(null, retainRecent: true);
            var host = GmWebServer.RunWithRechargeAsync(sharedPort, sharedPort, data, stop.Token, capture,
                new ServerOptions { ListenAddress = "127.0.0.2", GmWebPort = sharedPort }, started: ready.SetResult);
            try
            {
                await AwaitWebStart(host, ready.Task);
                using var http = WebClient("127.0.0.2", sharedPort);
                That(await ready.Task && (await http.GetAsync("/client/includeImg.htm")).StatusCode == HttpStatusCode.OK &&
                    (await http.GetAsync("/api/status")).StatusCode == HttpStatusCode.OK &&
                    (await http.GetAsync("/api/packets/status")).StatusCode == HttpStatusCode.OK,
                    "管理、包查询及客户端网页统一使用TCP监听地址，相同端口只绑定一次");
            }
            finally { stop.Cancel(); await host; }
        }
    }

    static int FreeWebPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    static HttpClient WebClient(string address, int port) => new(new HttpClientHandler { UseProxy = false })
        { BaseAddress = new Uri($"http://{address}:{port}"), Timeout = TimeSpan.FromSeconds(5) };

    static async Task AwaitWebStart(Task host, Task ready)
    {
        await await Task.WhenAny(host, ready).WaitAsync(TimeSpan.FromSeconds(10));
        That(ready.IsCompletedSuccessfully, "网页启动或降级必须结束就绪等待");
    }
}
