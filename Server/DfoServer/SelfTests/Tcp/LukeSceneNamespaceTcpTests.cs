using System.Buffers.Binary;
using System.Text;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

// 实际socket/登录/选角与GameProtocol日志；不安装scene消费者、不伪造会话Ready或owner。
internal static class LukeSceneNamespaceTcpTests
{
    const string Id = "a17c39e50123456789abcdef10293847";
    const string Receipt = "#cp-luke-scene/1 " + Id;

    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "luke-scene-namespace"), resources: resources);
        await fixture.StartAsync(packetCapture: true);
        using var observer = Open(fixture, "20081");
        observer.Create("SceneWatch");
        observer.Select();
        observer.Barrier();
        using var first = Open(fixture, "20082");
        // 仅完成真实LOGIN、尚未选角：真实状态门拒绝后由GameProtocol写unimplemented。
        first.Send(17, Body(Receipt));
        first.Send(17, Truncated());
        first.Create("SceneSend"); // 严格创建ACK是同连接前两包已分派的有序屏障。
        first.Select();
        first.Barrier();
        observer.Barrier();

        // 通过真实离房命令进入同一公共Town，不能用各账号私房隔离冒充广播拒绝。
        Leave(observer);
        Leave(first);
        Settle(observer);
        first.Send(17, Truncated());
        first.Send(17, Body("#cp-luke-scene/1 " + Id + new string('x', 300), kind: 13));
        first.Send(17, Body(Receipt));
        first.Barrier(); // 不接受任何机器响应或聊天回显，不能只用超时证明静默。
        observer.Barrier(); // 同频道接收者也不能收到机器广播。
        Echo(first, observer);

        using var replacement = Open(fixture, "20082");
        first.Closed(); // 只证明真实重复登录使旧socket关闭，不宣称测试了关闭后迟到包分派。
        observer.Expect(0, 6, Build(writer => writer.Write(first.Id)));
        Settle(observer); // 旧实体离开必须先消费，不能将其混入机器包的静默断言。
        replacement.Send(17, Truncated());
        replacement.Select(); // 真实选角序列作为未WorldReady机器包的有序屏障。
        replacement.Barrier();
        Leave(replacement);
        Settle(observer);
        replacement.Send(17, Body(Receipt));
        replacement.Barrier();
        observer.Barrier();
        Echo(replacement, observer); // 新owner仍为真实可投递的同场景成员。
        await fixture.StopAsync();

        var events = fixture.Events();
        var received = events.Where(row => row.GetProperty("Kind").GetString() == "recv" &&
            row.GetProperty("Detail").GetProperty("Opcode").GetUInt16() == 17).ToArray();
        That(received.Length == 9, "真实GameProtocol收到7个保留包与2个正常近似包");
        That(received.Count(row => row.GetProperty("Detail").GetProperty("Body").GetString() == "[luke-scene-reserved-redacted]") == 7,
            "真实recv在状态门之前已脱敏全部机器正文");
        var unimplemented = events.Where(row => row.GetProperty("Kind").GetString() == "unimplemented" &&
            row.GetProperty("Detail").GetProperty("Opcode").GetUInt16() == 17).ToArray();
        That(unimplemented.Length == 3 && unimplemented.All(row =>
            row.GetProperty("Detail").GetProperty("Body").GetString() == "[luke-scene-reserved-redacted]"),
            "三个未WorldReady包由真实分派写脱敏unimplemented");
        That(events.Count(row => row.GetProperty("Kind").GetString() == "luke_scene_reserved") == 4,
            "四个WorldReady包实际到达专用拒绝handler，不借限流或未就绪掩盖");
        string log = File.ReadAllText(Path.Combine(fixture.Options.LogDirectory, "server.jsonl"));
        string packets = File.ReadAllText(Path.Combine(fixture.Options.LogDirectory, "packet_log.txt"));
        string hex = Convert.ToHexString(Encoding.ASCII.GetBytes(Id));
        string spaced = BitConverter.ToString(Encoding.ASCII.GetBytes(Id)).Replace('-', ' ');
        That(!log.Contains(Id, StringComparison.Ordinal) && !log.Contains(hex, StringComparison.OrdinalIgnoreCase),
            "真实网络serverlog不含机器ID原文或hex");
        That(!packets.Contains(Id, StringComparison.Ordinal) && !packets.Contains(hex, StringComparison.OrdinalIgnoreCase) &&
            !packets.Contains(spaced, StringComparison.OrdinalIgnoreCase), "真实网络抓包body/raw不含机器ID");
        That(packets.Split("[luke scene reserved payload redacted]", StringSplitOptions.None).Length - 1 == 7,
            "真实网络抓包七个机器包全部仅留脱敏元数据");
        Console.WriteLine($"PASS {Count - before} scene namespace真实TCP断言（无能力协商/native/恢复）");
    }

    static void Echo(TcpPeer sender, TcpPeer observer)
    {
        const string text = "#cp-luke-scenex";
        sender.Send(17, Body(text));
        var expected = Build(writer =>
        {
            writer.Write((byte)3); writer.Write((ushort)0); writer.Write((byte)0);
            writer.Write((uint)text.Length); writer.Write(Encoding.ASCII.GetBytes(text));
        });
        Bytes(sender.Expect(0, 12).Body, expected, "普通近似根本人真实回显未被劫持");
        Bytes(observer.Expect(0, 12).Body, expected, "同场景旁观者收到精确近似根，证明其具备投递资格");
        sender.Barrier(); observer.Barrier();
    }

    static void Leave(TcpPeer peer)
    {
        peer.Send(1426);
        Settle(peer);
    }

    // 仅用于真实离房/接管引起的presence收尾；机器包仍使用原严格Barrier，不放宽共享白名单。
    static void Settle(TcpPeer peer)
    {
        peer.SendBarrier();
        for (int guard = 0; guard < 64; guard++)
        {
            var packet = peer.Read();
            if (packet.Kind == 1 && packet.Opcode == 1347)
            {
                Bytes(packet.Body, [1], "同屏收尾屏障成功正文");
                return;
            }
            That(packet.Kind == 0 && packet.Opcode is 2 or 3 or 4 or 22 or 23 or 24,
                $"同屏收尾仅允许presence/state帧，实际({packet.Kind},{packet.Opcode})");
        }
        That(false, "同屏收尾有界完成");
    }

    static byte[] Body(string text, byte kind = 3)
    {
        byte[] content = Encoding.ASCII.GetBytes(text);
        var body = new byte[11 + content.Length]; body[0] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), (uint)content.Length);
        content.CopyTo(body, 11); return body;
    }
    static byte[] Truncated()
    {
        var body = Body("#cp-luke-scenex" + Id);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), 14);
        return body;
    }
}
