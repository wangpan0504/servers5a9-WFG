using System.Text;
using System.Text.Json;

namespace DfoServer.Testing;

internal static class PacketCaptureTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "packet-capture"));
        await fixture.StartAsync();
        using (var peer = TcpPeer.Open(fixture))
        {
            peer.Send(3, [1]);
            peer.Expect(1, 3, [1]);
        }
        await fixture.StopAsync();
        TestCheck.That(!File.Exists(Path.Combine(fixture.Options.LogDirectory, "packet_log.txt")), "capture off creates no packet file");
        string disabledOutput = File.ReadAllText(Path.Combine(fixture.Options.LogDirectory, "stdout.txt"));
        TestCheck.That(disabledOutput.Contains("GM control: disabled") && disabledOutput.Contains("Packet HTTP: disabled"),
            "startup stdout explicitly reports disabled management endpoints");
        await fixture.StartAsync("enabled", packetCapture: true);
        ChannelTcpTests.Run(fixture);
        using (var peer = TcpPeer.Open(fixture))
        {
            peer.Send(3, [1]);
            peer.Expect(1, 3, [1]);
        }
        using (var earlyExit = new TcpPeer(fixture.Options.GamePort))
        {
            earlyExit.Expect(0, 1);
            earlyExit.Send(3, [1]);
            earlyExit.Closed();
        }
        await fixture.StopAsync();
        string text = File.ReadAllText(Path.Combine(fixture.Options.LogDirectory, "packet_log.txt"));
        foreach (string marker in new[] { "protocol=CHANNEL RECV", "protocol=CHANNEL SEND", "SEND kind=0x00 opcode=0x0001 name=CHANNELINFO", "RECV kind=0x01 opcode=0x061A name=LOGIN_PRECHECK", "RECV kind=0x01 opcode=0x0001 name=LOGIN", "SEND kind=0x01 opcode=0x061A", "SEND kind=0x01 opcode=0x0003 name=EXIT", "RECV kind=0x01 opcode=0x0003 name=EXIT len=14 body=(1B) [01]" })
            TestCheck.That(text.Contains(marker), "capture covers " + marker);
        TestCheck.That(text.Split("RECV kind=0x01 opcode=0x0003").Length == 3, "capture includes rejected pre-authentication traffic");
        string credential = "e10adc3949ba59abbe56e057f20f883e";
        TestCheck.That(!text.Contains(credential) && !text.Contains(BitConverter.ToString(Encoding.ASCII.GetBytes(credential)).Replace('-', ' ')), "raw and body omit credential bytes");
        TestCheck.That(text.Contains("[authentication payload redacted]"), "redaction is explicit in capture");

        await using var httpFixture = new TcpTestFixture(source, Path.Combine(root, "packet-http"), packetHttp: true);
        await httpFixture.StartAsync();
        using var http = new HttpClient { BaseAddress = new($"http://127.0.0.1:{httpFixture.Options.PacketHttpPort}"), Timeout = TimeSpan.FromSeconds(5) };
        TestCheck.That((await Read("/health")).GetProperty("status").GetString() == "ok", "真实服务启动HTTP只读入口");
        var initial = await Read("/api/status");
        TestCheck.That(initial.GetProperty("activeSessions").GetInt32() == 0 && initial.GetProperty("receivedPackets").GetInt64() == 0 &&
            initial.GetProperty("packetHttpPort").GetInt32() == httpFixture.Options.PacketHttpPort &&
            (await Read("/api/sessions")).GetArrayLength() == 0 && (await Read("/api/packets")).GetArrayLength() == 0,
            "真实实例只返回本次连接和历史，不读取既有文件抓包");
        ChannelTcpTests.Run(httpFixture);
        using (var peer = TcpPeer.Open(httpFixture))
        {
            peer.Send(3, [1]);
            peer.Expect(1, 3, [1]);
        }
        var history = await Read("/api/packets");
        var rows = history.EnumerateArray().ToArray();
        int clientId = rows.Single(row => row.GetProperty("protocol").GetString() == "GAME" && row.GetProperty("opcode").GetInt32() == 3 &&
            row.GetProperty("direction").GetString() == "RECV").GetProperty("connectionId").GetInt32();
        var filtered = await Read("/api/packets?connectionId=" + clientId);
        TestCheck.That(filtered.GetArrayLength() != 0 && filtered.EnumerateArray().All(row => row.GetProperty("connectionId").GetInt32() == clientId) &&
            rows.Any(row => row.GetProperty("connectionId").GetInt32() != clientId), "按真实客户端过滤不会混入频道或其他连接");
        TestCheck.That(filtered.EnumerateArray().Any(row => row.GetProperty("opcode").GetInt32() == 3 &&
            row.GetProperty("direction").GetString() == "RECV" && row.GetProperty("bodyHex").GetString() == "01"),
            "客户端过滤保留实收请求正文");
        TestCheck.That(rows.Any(row => row.GetProperty("opcode").GetInt32() == 1 && row.GetProperty("redacted").GetBoolean() &&
            row.GetProperty("bodyHex").GetString() == "") && !history.GetRawText().Contains(credential, StringComparison.OrdinalIgnoreCase),
            "真实认证在HTTP历史中使用同一脱敏规则");
        using (var peer = TcpPeer.Open(httpFixture))
        {
            var sessions = await Read("/api/sessions");
            TestCheck.That(sessions.EnumerateArray().Any(session => session.GetProperty("protocol").GetString() == "GAME" &&
                session.GetProperty("receivedPackets").GetInt64() >= 2 &&
                session.GetProperty("remoteEndpoint").GetString()!.StartsWith("127.0.0.1:", StringComparison.Ordinal)),
                "客户端目录关联当前活跃传输及计数");
            TestCheck.That((await Read("/api/characters/online")).GetArrayLength() == 0, "名册连接不等于在线角色");
            peer.Create("HttpOnline");
            peer.Select();
            bool ready = false;
            for (int attempt = 0; attempt < 20 && !ready; attempt++)
            {
                var online = await Read("/api/characters/online");
                ready = online.EnumerateArray().Any(character => character.GetProperty("name").GetString() == "HttpOnline" &&
                    character.GetProperty("characterId").GetInt32() == peer.Id && character.GetProperty("ready").GetBoolean() &&
                    character.GetProperty("phase").GetString() == "Town" && character.GetProperty("userState").GetString() == "Town");
                if (!ready) await Task.Delay(25);
            }
            TestCheck.That(ready, "在线查询读取现有选角、就绪与user状态");
        }
        bool cleared = false;
        for (int attempt = 0; attempt < 20 && !cleared; attempt++)
        {
            cleared = (await Read("/api/characters/online")).GetArrayLength() == 0 && (await Read("/api/sessions")).GetArrayLength() == 0;
            if (!cleared) await Task.Delay(25);
        }
        TestCheck.That(cleared, "断线清理同一会话目录与客户端摘要");
        await httpFixture.StopAsync();
        TestCheck.That(!File.Exists(Path.Combine(httpFixture.Options.LogDirectory, "packet_log.txt")), "HTTP-only不启用文件抓包");
        TestCheck.That(File.ReadAllText(Path.Combine(httpFixture.Options.LogDirectory, "stdout.txt")).Contains(
            $"Packet HTTP: 127.0.0.1:{httpFixture.Options.PacketHttpPort}"), "启动日志记录本次实际HTTP端点");

        async Task<JsonElement> Read(string path)
        {
            using var json = JsonDocument.Parse(await http.GetStringAsync(path));
            return json.RootElement.Clone();
        }
    }
}
