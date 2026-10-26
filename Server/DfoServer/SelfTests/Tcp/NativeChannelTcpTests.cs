using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class NativeChannelTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "native-channel"), resources: resources);
        await fixture.StartAsync(packetCapture: true);
        using var main = Open(fixture, "nativea");
        int port = checked((int)U32(main.LoginEndpoint, 13 + checked((int)U32(main.LoginEndpoint, 9))));
        uint token = U32(main.LoginEndpoint, 5);
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        That(token != 0 && port != fixture.Options.ChannelPort && !endpoints.Values.Contains(port),
            "认证回包实际发行关联值和独立Native监听，不改变频道目录");
        using var first = await Connect(port);
        That(first.Available == 0, "Native连接不发送普通游戏CHANNELINFO首包");
        await Send(first, token);
        await Seen(fixture, "native_associated", 1);
        await Seen(fixture, "native_unimplemented", 1);
        That(first.Available == 0, "尚未接入真实事件的载荷不伪造成功应答");
        ProbeMain(main);
        string before = fixture.Snapshot();
        using var invalid = await Connect(port);
        await Send(invalid, 0);
        await Closed(invalid);
        ProbeMain(main);

        using var replacement = await Connect(port);
        await Send(replacement, token);
        await Seen(fixture, "native_associated", 2);
        await Closed(first);
        // 同一主连接下新Native替换旧Native，旧finally不能清掉新租约。
        await Send(replacement, token);
        ProbeMain(main);
        using var otherMain = Open(fixture, "nativeb");
        using var other = await Connect(port);
        await Send(other, U32(otherMain.LoginEndpoint, 5));
        await Seen(fixture, "native_associated", 3);
        ProbeMain(otherMain);

        using var takeover = Open(fixture, "nativea");
        main.Closed();
        await Closed(replacement);
        using var stale = await Connect(port);
        await Send(stale, token);
        await Closed(stale);
        uint nextToken = U32(takeover.LoginEndpoint, 5);
        using var current = await Connect(port);
        await Send(current, nextToken);
        await Seen(fixture, "native_associated", 4);
        ProbeMain(takeover); ProbeMain(otherMain);

        await Send(current, 0);
        await Closed(current);
        ProbeMain(takeover);
        using var malformed = await Connect(port);
        byte[] bad = Frame(nextToken); bad[12] = 2;
        await malformed.GetStream().WriteAsync(bad);
        await Closed(malformed);
        ProbeMain(takeover);
        That(fixture.Snapshot() == before, "Native接受、拒绝、重绑均不伪造任务进度或写入角色");

        takeover.Create("NativeRole"); takeover.Select(); takeover.Barrier();
        string selected = fixture.Snapshot();
        using var final = await Connect(port);
        await Send(final, nextToken);
        await Seen(fixture, "native_associated", 5);
        takeover.Barrier();
        That(fixture.Snapshot() == selected, "选角后仍绑定原Session，未实现Native载荷不改变已选角色");
        takeover.Send(3, [1]); takeover.Expect(1, 3, [1]); takeover.Closed();
        await Closed(final);
        ProbeMain(otherMain);
        await Send(other, U32(otherMain.LoginEndpoint, 5));
        otherMain.Send(3, [1]); otherMain.Expect(1, 3, [1]); otherMain.Closed();
        await Closed(other);
        await fixture.StopAsync();
        string capture = File.ReadAllText(Path.Combine(fixture.Options.LogDirectory, "packet_log.txt"));
        That(capture.Contains("protocol=NATIVE") && capture.Contains("native association and payload redacted") &&
            !capture.Contains("native-secret") && !capture.Contains("6E 61 74 69 76 65 2D 73 65 63 72 65 74"),
            "真实Native接入日志只留长度，正文和关联材料不进入抓包");
        var events = fixture.Events();
        That(events.Count(e => e.GetProperty("Kind").GetString() == "native_associated") == 5 &&
            !File.ReadAllText(Path.Combine(fixture.Options.LogDirectory, "server.jsonl")).Contains("native-secret"),
            "过期、零关联和坏帧没有注册新连接，服务日志不输出载荷");
    }

    static void ProbeMain(TcpPeer peer)
    {
        // 未选角阶段不发送场景1347；使用现有只读重名检查验证主连接响应。
        peer.Send(692, Name("NativeProbe"));
        peer.Expect(1, 692, [1]);
    }

    static async Task<TcpClient> Connect(int port)
    {
        var client = new TcpClient { NoDelay = true };
        try { await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(3)); return client; }
        catch { client.Dispose(); throw; }
    }
    // 独立组成13字节外层；不借用服务端Parser/Builder生成自己的期望。
    static byte[] Frame(uint token)
    {
        byte[] body = "native-secret"u8.ToArray();
        byte[] frame = new byte[13 + body.Length]; frame[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)frame.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), token);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), 0x90abcdef);
        body.CopyTo(frame, 13);
        return frame;
    }
    static async Task Send(TcpClient peer, uint token) => await peer.GetStream().WriteAsync(Frame(token));
    static async Task Closed(TcpClient peer)
    {
        try
        {
            byte[] one = new byte[1];
            int count = await peer.GetStream().ReadAsync(one).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            That(count == 0, "Native连接关闭且未发送猜测的ACK");
        }
        catch (IOException error) when (error.InnerException is SocketException { SocketErrorCode:
            SocketError.ConnectionReset or SocketError.ConnectionAborted })
        { That(true, "Native拒绝后关闭连接"); }
    }
    internal static async Task Seen(TcpTestFixture fixture, string kind, int count)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(5))
        {
            // 日志后台写入，只读取已完成的行；不把半条JSON误报为业务故障。
            using var reader = new StreamReader(new FileStream(Path.Combine(fixture.Options.LogDirectory, "server.jsonl"),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            string text = await reader.ReadToEndAsync();
            int end = text.LastIndexOf('\n');
            int seen = 0;
            foreach (string line in text[..(end + 1)].Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var row = JsonDocument.Parse(line);
                if (row.RootElement.GetProperty("Kind").GetString() == kind) seen++;
            }
            if (seen >= count) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("Native event not observed: " + kind + "/" + count);
    }
}
