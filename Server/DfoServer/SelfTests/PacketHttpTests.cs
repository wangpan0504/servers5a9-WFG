using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Management;
using DfoServer.GameWorld.Channels;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PacketHttpTests
{
    public static async Task RunAsync()
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(), "packet-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            var options = new ServerOptions { PacketHttpPort = port };
            using var capture = new PacketCapture(null, retainRecent: true);
            using var log = new ServerLog(root);
            using var stop = new CancellationTokenSource();
            bool failOnline = false;
            OnlineCharacterView[] Online() => failOnline ? throw new InvalidOperationException("private-error-detail") :
                [new("query", 7, "QueryRole", true, "Town", "Town")];
            Task running = PacketHttpServer.Run(port, capture, options, Online, DateTimeOffset.Now, log, stop.Token);
            var partial = new List<TcpClient>();
            using var http = new HttpClient { BaseAddress = new($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(5) };
            try
            {
                Reject<SocketException>(() => PacketHttpServer.Run(port, capture, options, Online, DateTimeOffset.Now, log, stop.Token),
                    "占用HTTP端口明确拒绝，不误报告已就绪");
                That((await Read("/health")).GetProperty("status").GetString() == "ok", "只读HTTP健康入口");
                That((await Read("/api/packets")).GetArrayLength() == 0 && (await Read("/api/sessions")).GetArrayLength() == 0,
                    "无客户端时不伪造传输或历史数据");
                That((await Read("/api/characters/online"))[0].GetProperty("name").GetString() == "QueryRole",
                    "在线角色查询仅消费既有会话提供的快照");
                capture.Register(21, false, new(IPAddress.Loopback, 12021));
                capture.Register(22, true, new(IPAddress.Loopback, 12022));
                capture.Write(21, false, true, GameFrame.Build(0, 2, 0, [0xab, 0xcd]));
                capture.Write(21, false, false, GameFrame.Build(1, 1, 0, "private-credential"u8));
                byte[] channel = new byte[12];
                channel[1] = 11;
                channel[11] = 0xcc;
                capture.Write(22, true, false, channel);
                using (var response = await http.GetAsync("/api/packets"))
                {
                    string json = await response.Content.ReadAsStringAsync();
                    That(response.IsSuccessStatusCode && response.Headers.CacheControl?.NoStore == true &&
                        response.Headers.GetValues("X-Content-Type-Options").Single() == "nosniff", "包记录不缓存且JSON不作其他内容执行");
                    That(!json.Contains("private-credential", StringComparison.Ordinal), "HTTP正文不泄露认证字节");
                }
                var packets = await Read("/api/packets?connectionId=21");
                That(packets.GetArrayLength() == 2 && packets.EnumerateArray().All(row => row.GetProperty("connectionId").GetInt32() == 21),
                    "HTTP按客户端连接隔离包记录");
                That(packets[0].GetProperty("bodyHex").GetString() == "ABCD" && packets[1].GetProperty("redacted").GetBoolean() &&
                    packets[1].GetProperty("bodyHex").GetString() == "", "过滤不改变记录顺序、正文及认证脱敏");
                That((await Read("/api/packets?connectionId=999")).GetArrayLength() == 0 &&
                    (await Read("/api/packets?connectionId=%32%32")).GetArrayLength() == 1, "未知连接为空，URL编码使用标准查询解析");
                var sessions = await Read("/api/sessions");
                That(sessions.GetArrayLength() == 2 && sessions[0].GetProperty("sentPackets").GetInt64() == 1 &&
                    sessions[0].GetProperty("remoteEndpoint").GetString() == "127.0.0.1:12021", "连接目录提供客户端标识及本次计数");
                capture.Remove(21);
                That((await Read("/api/packets?connectionId=21")).GetArrayLength() == 2, "已关闭客户端仍能查询保留历史");
                var status = await Read("/api/status");
                That(status.GetProperty("activeSessions").GetInt32() == 1 && status.GetProperty("activeGameSessions").GetInt32() == 0 &&
                    status.GetProperty("totalConnections").GetInt64() == 2 && status.GetProperty("receivedPackets").GetInt64() == 2 &&
                    status.GetProperty("sentPackets").GetInt64() == 1 && status.GetProperty("packetHttpPort").GetInt32() == port,
                    "状态计数为本次实例的完整帧和活跃传输，不混同在线角色");
                foreach (string query in new[] { "connectionId=", "connectionId=0", "connectionId=-1", "connectionId=2147483648",
                    "connectionId=1.0", "connectionId=21&connectionId=22", "connectionId=21&extra=1", "client=21" })
                {
                    using var response = await http.GetAsync("/api/packets?" + query);
                    That(response.StatusCode == HttpStatusCode.BadRequest, "非法或重复客户端条件明确拒绝：" + query);
                }
                using (var post = await http.PostAsync("/api/packets", new StringContent("")))
                    That(post.StatusCode == HttpStatusCode.MethodNotAllowed && post.Content.Headers.Allow.Contains("GET"), "只读入口拒绝写方法并声明GET");
                using (var post = await http.PostAsync("/api/packets", new StringContent("unused")))
                    That(post.StatusCode == HttpStatusCode.MethodNotAllowed, "写方法携带正文仍不执行任何业务");
                using (var missing = await http.GetAsync("/missing"))
                    That(missing.StatusCode == HttpStatusCode.NotFound, "未实现HTTP路径不返回通用成功");
                using (var evil = new HttpRequestMessage(HttpMethod.Get, "/api/packets"))
                {
                    evil.Headers.Host = "evil.invalid";
                    using var response = await http.SendAsync(evil);
                    That(response.StatusCode == HttpStatusCode.Forbidden, "本机监听仍拒绝非本机Host");
                }
                using (var evil = new HttpRequestMessage(HttpMethod.Get, "/api/packets"))
                {
                    evil.Headers.Add("Origin", "http://evil.invalid");
                    using var response = await http.SendAsync(evil);
                    That(response.StatusCode == HttpStatusCode.Forbidden, "GM同类的Origin检查拒绝跨站读取");
                }
                foreach (string fields in new[] { $"Host: 127.0.0.1:{port}\r\nHost: localhost:{port}",
                    $"Host: 127.0.0.1:{port}\r\nTransfer-Encoding: chunked", $"Host: 127.0.0.1:{port}\r\nContent-Length: 1",
                    $"Host: 127.0.0.1:{port}\r\nBadHeader", $"Host: 127.0.0.1:{port}\r\nContent-Length: -1" })
                    That(await Raw("GET /api/packets HTTP/1.1\r\n" + fields + "\r\n\r\n") == 400, "畸形头或请求正文不触发查询");
                That(await Raw("GET /api/packets HTTP/1.1\r\nHost: " + new string('x', 8192)) == 400,
                    "HTTP请求头固定上限不形成无界缓冲");
                failOnline = true;
                using (var response = await http.GetAsync("/api/characters/online"))
                    That(response.StatusCode == HttpStatusCode.InternalServerError &&
                        !(await response.Content.ReadAsStringAsync()).Contains("private-error-detail", StringComparison.Ordinal), "查询失败明确报错但不泄露内部详情");
                failOnline = false;
                var slow = new TcpClient();
                partial.Add(slow);
                await slow.ConnectAsync(IPAddress.Loopback, port);
                await slow.GetStream().WriteAsync("GET /api/packets HTTP/1.1\r\n"u8.ToArray());
                That((await Read("/health")).GetProperty("status").GetString() == "ok", "慢HTTP请求不串行阻塞健康查询");
                for (int i = 1; i < 8; i++)
                {
                    var client = new TcpClient();
                    partial.Add(client);
                    await client.ConnectAsync(IPAddress.Loopback, port);
                    await client.GetStream().WriteAsync("GET /health HTTP/1.1\r\n"u8.ToArray());
                }
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(3));
                foreach (var client in partial) client.Dispose();
            }
            That(running.IsCompletedSuccessfully && !File.Exists(Path.Combine(root, "packet_log.txt")),
                "取消会排空有限HTTP连接，HTTP-only不创建抓包文件");
            using var restart = new CancellationTokenSource();
            using var fresh = new PacketCapture(null, retainRecent: true);
            Task restarted = PacketHttpServer.Run(port, fresh, options, Online, DateTimeOffset.Now, log, restart.Token);
            try
            {
                That((await Read("/api/packets")).GetArrayLength() == 0, "关闭后可重用同一HTTP端口，新实例不继承旧包");
            }
            finally
            {
                restart.Cancel();
                await restarted.WaitAsync(TimeSpan.FromSeconds(3));
            }
            var catalog = ChannelCatalog.Parse("[server]\n2\n" + string.Join('\n', new[] { 10, 11, 12 }.Select(number =>
                $"{number} `<chn_channel_info_028>` 1 `[granfloris]` 5 0 0 0 0 0 0 0 0 0 0 ``")) + "\n[/server]\n");
            var listeners = new GameChannelListeners(options with { ChannelEntryPortBase = port - 1 }, catalog);
            Reject<InvalidDataException>(listeners.Start, "分类入口连续端口区间不能覆盖HTTP保留端口");
            That(listeners.Bound.Count == 0, "端口区间冲突在启动任何分类入口前拒绝");

            async Task<JsonElement> Read(string path)
            {
                using var json = JsonDocument.Parse(await http.GetStringAsync(path));
                return json.RootElement.Clone();
            }
            async Task<int> Raw(string request)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request), deadline.Token);
                using var reader = new StreamReader(client.GetStream());
                string line = await reader.ReadLineAsync(deadline.Token) ?? throw new IOException("No HTTP response");
                return int.Parse(line.Split(' ')[1]);
            }
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - checks} 本机包查询HTTP、客户端过滤、安全边界及取消断言");
    }
}
