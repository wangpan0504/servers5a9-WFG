using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ServerLauncher;

// 启动器独有的无资源回归；仅使用隔离监听与合成GM，不读写运行中的玩家库。
internal static class GmWebTests
{
    public static async Task RunAsync()
    {
        int checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("FAIL: " + message);
            checks++;
        }
        void Reject(Action action, string message)
        {
            bool rejected = false;
            try { action(); }
            catch (Exception error) when (error is IOException or InvalidDataException) { rejected = true; }
            Check(rejected, message);
        }
        var old = JsonSerializer.Deserialize<LaunchSettings>("""
            {"ServerIp":"127.0.0.1","ChannelPort":23100,"GamePort":23101,"PvfPath":"Data/Pvf/Script.pvf","DataDirectory":"Data"}
            """)!;
        old.Validate();
        Check(!old.GmEnabled && old.ActiveGmPort == 0 && old.GmPort == 23102 && old.GmWebPort == 23103,
            "旧配置缺少GM字段时保持关闭且恢复默认端口");
        var enabled = old with { GmEnabled = true, GmPort = 24002, GmWebPort = 24003 };
        enabled.Validate();
        Check(JsonSerializer.Deserialize<LaunchSettings>(JsonSerializer.Serialize(enabled)) == enabled,
            "GM开关和自定义端口按旧设置文件格式保存回放");
        foreach (var invalid in new[] { enabled with { GmPort = 0 }, enabled with { GmWebPort = 65536 },
            enabled with { GmPort = old.GamePort }, enabled with { GmWebPort = enabled.GmPort } })
            Reject(invalid.Validate, "启用GM的所有端口须有效且不重复");
        (old with { GmPort = old.GamePort, GmWebPort = old.GamePort }).Validate();
        Check(true, "关闭GM时不增加无关的端口占用限制");

        var gm = new TcpListener(IPAddress.Loopback, 0);
        gm.Start();
        int gmPort = ((IPEndPoint)gm.LocalEndpoint).Port;
        int webPort = FreePort();
        var received = new List<string>();
        var logs = new List<string>();
        using var stop = new CancellationTokenSource();
        var answering = AnswerAsync();
        bool available = true;
        int expectedProcess = Environment.ProcessId;
        await using var host = new GmWebHost(gmPort, webPort, value => logs.Add(value), () => available,
            client => LocalConnectionOwner.Matches(client, expectedProcess));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            var meta = await http.GetStringAsync(host.Url + "/api/meta");
            using (var parsed = JsonDocument.Parse(meta))
                Check(parsed.RootElement.GetProperty("gmPort").GetInt32() == gmPort &&
                    parsed.RootElement.GetProperty("webPort").GetInt32() == webPort &&
                    parsed.RootElement.GetProperty("account").GetString() == "10000", "元数据指向本次GM端口");
            var page = await http.GetStringAsync(host.Url);
            Check(page.Contains("<title>开发 GM</title>", StringComparison.Ordinal) && page.Contains("/api/command", StringComparison.Ordinal),
                "网页从程序内嵌资源读取，无Python或外置html依赖");
            foreach (string line in new[] { "account 10000 list", "account 10000 gold 测角 12345", "infinite-fatigue", "cash-shop-expiration" })
            {
                using var response = await http.PostAsync(host.Url + "/api/command", Json(line));
                string text = await response.Content.ReadAsStringAsync();
                Check(response.StatusCode == HttpStatusCode.OK && text.Contains("OK", StringComparison.Ordinal), "UTF8命令与中文回复透传");
            }
            Check(received.SequenceEqual(new[] { "account 10000 list", "account 10000 gold 测角 12345", "infinite-fatigue", "cash-shop-expiration" }),
                "网页命令无自动重试或重复写入");
            foreach (string line in new[] { "", "list\nitem", new string('x', 513) })
            {
                using var response = await http.PostAsync(host.Url + "/api/command", Json(line));
                Check(response.StatusCode == HttpStatusCode.BadRequest, "畸形命令拒绝且不转发GM");
            }
            foreach (string json in new[] { "[]", "null", "{", "{\"line\":42}" })
            {
                using var response = await http.PostAsync(host.Url + "/api/command", new StringContent(json, Encoding.UTF8, "application/json"));
                Check(response.StatusCode == HttpStatusCode.BadRequest, "JSON形状与类型拒绝");
            }
            using (var request = new HttpRequestMessage(HttpMethod.Post, host.Url + "/api/command") { Content = Json("list") })
            {
                request.Headers.Add("Origin", "http://evil.invalid");
                using var response = await http.SendAsync(request);
                Check(response.StatusCode == HttpStatusCode.Forbidden, "跨站Origin不允许执行GM命令");
            }
            using (var request = new HttpRequestMessage(HttpMethod.Get, host.Url + "/api/meta"))
            {
                request.Headers.Host = "evil.invalid";
                using var response = await http.SendAsync(request);
                Check(response.StatusCode == HttpStatusCode.Forbidden, "非本机Host拒绝");
            }
            using (var missing = await http.GetAsync(host.Url + "/missing"))
                Check(missing.StatusCode == HttpStatusCode.NotFound, "未知路径不返回成功");
            foreach (string fields in new[] { "Content-Length: 5000\r\n", "Content-Length: 1\r\nContent-Length: 1\r\n", "Transfer-Encoding: chunked\r\n" })
            {
                using var raw = new TcpClient();
                await raw.ConnectAsync(IPAddress.Loopback, webPort);
                var stream = raw.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /api/command HTTP/1.1\r\nHost: 127.0.0.1:{webPort}\r\n{fields}\r\n"));
                using var reader = new StreamReader(stream);
                Check((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))?.Contains("400", StringComparison.Ordinal) == true,
                    "过长正文、重复长度或分块正文不绕过限制");
            }
            Reject(() => new GmWebHost(gmPort, webPort, _ => { }), "网页端口冲突明确拒绝而不复用未知服务");
            Check(received.Count == 4, "所有拒绝请求均不触发资产命令");
            using (var response = await http.PostAsync(host.Url + "/api/command", Json("truncated")))
                Check(response.StatusCode == HttpStatusCode.BadGateway, "GM半行回复后断开不能当作成功");
            int receivedBefore = received.Count;
            expectedProcess = 1;
            using (var response = await http.PostAsync(host.Url + "/api/command", Json("list")))
                Check(response.StatusCode == HttpStatusCode.BadGateway && received.Count == receivedBefore,
                    "端口所属PID变化时不发送命令，即使原进程仍活着");
            expectedProcess = Environment.ProcessId;
            available = false;
            using (var response = await http.PostAsync(host.Url + "/api/command", Json("list")))
                Check(response.StatusCode == HttpStatusCode.BadGateway && received.Count == receivedBefore,
                    "原服务退出后不代理可能被新进程复用的GM端口");
            available = true;
            stop.Cancel(); gm.Stop(); await answering;
            using (var response = await http.PostAsync(host.Url + "/api/command", Json("list")))
                Check(response.StatusCode == HttpStatusCode.BadGateway, "GM服务退出返回明确失败，不伪造成功");
            using var partial = new TcpClient();
            await partial.ConnectAsync(IPAddress.Loopback, webPort);
            await partial.GetStream().WriteAsync("GET / HTTP/1.1\r\n"u8.ToArray());
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            var probe = new TcpListener(IPAddress.Loopback, webPort);
            probe.Start(); probe.Stop();
            Check(true, "关闭网页取消未完成请求并释放监听端口");
            await using var reopened = new GmWebHost(gmPort, webPort, _ => { });
            Check((await http.GetAsync(reopened.Url + "/api/meta")).IsSuccessStatusCode, "同一端口重新打开，无旧请求或监听残留");
        }
        finally
        {
            stop.Cancel(); gm.Stop(); await answering;
        }
        Console.WriteLine($"PASS {checks} launcher GM settings and HTTP proxy assertions");

        async Task AnswerAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await gm.AcceptTcpClientAsync(stop.Token);
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                    string? line = await reader.ReadLineAsync(stop.Token);
                    if (line is null) continue;
                    received.Add(line);
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(line == "truncated" ? "OK count=" : "OK 中文返回 " + line + "\n"), stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
        }
    }

    static StringContent Json(string line) => new(JsonSerializer.Serialize(new { line }), Encoding.UTF8, "application/json");
    static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
