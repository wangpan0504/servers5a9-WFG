using System.Buffers.Binary;
using System.Net;
using System.Text;
using DfoServer.Game.Accounts;
using DfoServer.Network;
using DfoServer.Testing;

namespace DfoServer;

internal static class PacketCaptureTests
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "packet-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int checks = TestCheck.Count;
        try
        {
            using var capture = new PacketCapture(root);
            string path = Path.Combine(root, "packet_log.txt");
            using var stop = new CancellationTokenSource();
            using var stream = new MemoryStream();
            var connection = new ConnectionContext(12, IPAddress.Loopback, stream, stop, capture: capture);
            byte[] received = ClientFrame(65535, [0x10, 0x20, 0xfe]);
            stream.Write(received);
            stream.Position = 0;
            TestCheck.Bytes(await connection.ReadAsync(), received, "capture preserves the received frame");
            byte[] sent = GameFrame.Build(0, 2, 9, [0xab, 0xcd]);
            await connection.WriteAsync(sent);
            TestCheck.Bytes(stream.ToArray().AsSpan(received.Length), sent, "capture preserves the sent frame");
            capture.Flush();
            string text = ReadCapture(path);
            TestCheck.That(text.Contains("connection=12 protocol=GAME RECV kind=0x01 opcode=0xFFFF name=65535 len=16 body=(3B) [10 20 FE]"), "receive header is 13 bytes");
            TestCheck.That(text.Contains("SEND kind=0x00 opcode=0x0002 name=USERINFO len=18 body=(2B) [AB CD]"), "send header is 16 bytes");
            TestCheck.That(text.Contains("raw: " + BitConverter.ToString(sent).Replace('-', ' ')), "full wire frame is present");

            byte[] allBytes = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
            byte[] allByteFrame = GameFrame.Build(0, 65535, 0, allBytes);
            capture.Write(12, false, true, allByteFrame);
            capture.Write(12, false, true, GameFrame.Build(0, 65535, 0, []));
            capture.Flush();
            text = ReadCapture(path);
            TestCheck.That(text.Contains("body=(256B) [" + BitConverter.ToString(allBytes).Replace('-', ' ') + "]\n  raw: " +
                BitConverter.ToString(allByteFrame).Replace('-', ' ')), "全部字节值的正文和原帧保持大写十六进制及单空格分隔");
            TestCheck.That(text.Contains("body=(0B) []\n  raw: "), "空正文不输出多余字符");

            byte[] secret = Encoding.ASCII.GetBytes("credential-must-not-be-logged");
            foreach (ushort opcode in new ushort[] { 1, 1562, 171 })
                capture.Write(12, false, false, ClientFrame(opcode, secret));
            capture.Flush();
            text = ReadCapture(path);
            TestCheck.That(text.Split("[authentication payload redacted]").Length == 4, "all credential-bearing requests are redacted");
            TestCheck.That(!text.Contains(BitConverter.ToString(secret).Replace('-', ' ')), "credential absent from body and raw");
            byte[] channel = new byte[14];
            channel[1] = 11;
            channel[11] = 0xaa;
            channel[12] = 0xbb;
            channel[13] = 0xcc;
            capture.Write(13, true, false, channel);
            capture.Write(13, true, true, channel);
            capture.Flush();
            text = ReadCapture(path);
            TestCheck.That(text.Split("body=(3B) [AA BB CC]").Length == 3, "both channel directions use an 11-byte header");

            using var owner = new AccountOwners().Acquire("capture-test", () => { });
            using var senderStop = new CancellationTokenSource();
            using var senderBytes = new MemoryStream();
            await using (var sender = new GameConnectionSender(owner, senderBytes, senderStop, 7, (_, _, _, _, _) => { }, connection.CaptureSent))
                await sender.Send(1, 3, [1], "exit");
            capture.Flush();
            TestCheck.That(ReadCapture(path).Contains("SEND kind=0x01 opcode=0x0003 name=EXIT len=17 body=(1B) [01]"), "normal send pump is captured");
            TestCheck.Bytes(senderBytes.ToArray(), GameFrame.Build(1, 3, 7, [1]), "send pump bytes and sequence unchanged");

            string blocked = Path.Combine(root, "blocked");
            File.WriteAllText(blocked, "not a directory");
            using var failedCapture = new PacketCapture(blocked);
            TestCheck.That(!failedCapture.IsEnabled, "startup status reflects capture failure rather than the requested directory");
            using var faultBytes = new MemoryStream();
            var faultConnection = new ConnectionContext(14, IPAddress.Loopback, faultBytes, stop, capture: failedCapture);
            await faultConnection.WriteAsync(sent);
            TestCheck.Bytes(faultBytes.ToArray(), sent, "capture I/O failure does not close or change transport");

            using var history = new PacketCapture(null, retainRecent: true);
            history.Register(1, false, new(IPAddress.Loopback, 12001));
            history.Register(2, true, new(IPAddress.Loopback, 12002));
            history.Register(1, false, new(IPAddress.Loopback, 12003));
            history.Write(1, false, false, ClientFrame(65535, new byte[4097]));
            TestCheck.That(history.Recent() is [{ ConnectionId: 1, BodyLength: 4097, BodyTruncated: true, Redacted: false, BodyHex.Length: 8192 }],
                "内存包体保留最多4096字节，原长度与截断标记分开");
            foreach (ushort opcode in new ushort[] { 1, 1562, 171 })
                history.Write(1, false, false, ClientFrame(opcode, secret));
            TestCheck.That(history.Recent().Skip(1).All(packet => packet.Redacted && !packet.BodyTruncated && packet.BodyHex == ""),
                "内存查询与文件抓包共用全部认证正文脱敏");
            var retained = history.Recent();
            for (int i = 0; i < 402; i++)
                history.Write(i % 2 + 1, false, true, GameFrame.Build(0, 65535, 0, [(byte)(i >> 8), (byte)i]));
            TestCheck.That(history.Recent().Length == 400 && history.Recent()[0].BodyHex == "0002" && history.Recent()[^1].BodyHex == "0191",
                "全局固定容量按实际记录顺序淘汰最旧包，不为客户端建立无界历史");
            TestCheck.That(retained.Length == 4 && retained[0].BodyLength == 4097, "返回的历史快照不随后续记录修改");
            var filtered = history.Recent(1);
            TestCheck.That(filtered.Length == 200 && filtered.All(packet => packet.ConnectionId == 1) &&
                filtered[0].BodyHex == "0002" && filtered[^1].BodyHex == "0190", "客户端过滤保留该连接的原有包序");
            TestCheck.That(history.Recent(999).Length == 0 && history.TotalConnections == 2 && history.TotalReceived == 4 && history.TotalSent == 402,
                "未知客户端为空，不重复计算已注册连接，总计数不因历史淘汰丢失");
            history.Remove(1);
            TestCheck.That(history.Sessions() is [{ ConnectionId: 2 }] && history.Recent(1).Length == 200,
                "关闭连接仅移除在线传输摘要，已保留包仍可按原客户端查询");
            Parallel.For(0, 500, _ => history.Write(2, true, false, channel));
            TestCheck.That(history.Recent().Length == 400 && history.TotalReceived == 504 && history.Sessions()[0].ReceivedPackets == 500,
                "并发记录保持容量及连接计数边界");
            history.Dispose();
            history.Register(3, false, new(IPAddress.Loopback, 12003));
            history.Write(3, false, true, sent);
            TestCheck.That(history.Recent().Length == 0 && history.Sessions().Length == 0,
                "结束后释放记录与客户端摘要，迟到回调不复活旧历史");
            using var failedHistory = new PacketCapture(blocked, retainRecent: true);
            failedHistory.Write(1, false, true, sent);
            TestCheck.That(!failedHistory.IsEnabled && failedHistory.Recent().Length == 1,
                "文件抓包失败不关闭独立的HTTP内存记录");
            using var native = new PacketCapture(Path.Combine(root, "native"), retainRecent: true);
            native.Register(7, false, new(IPAddress.Loopback, 12007), native: true);
            native.Write(8, false, true, GameFrame.Build(1, 1, 2, secret));
            native.Write(8, false, true, GameFrame.Build(0, 1, 0, [0xab, 0xcd]));
            native.WriteNative(7, 32, 19);
            native.Flush();
            TestCheck.That(native.Recent() is [{ Redacted: true, BodyHex: "" }, { Redacted: false, BodyHex: "ABCD" },
                { Protocol: "NATIVE", BodyLength: 19, TotalLength: 32, Redacted: true, BodyHex: "" }],
                "LOGIN关联正文脱敏，NOTI1握手仍保留，Native仅留长度");
            TestCheck.That(native.Sessions() is [{ Protocol: "NATIVE", ReceivedPackets: 1 }], "Native传输使用独立协议摘要");
            string nativeText = ReadCapture(Path.Combine(root, "native", "packet_log.txt"));
            TestCheck.That(!nativeText.Contains(BitConverter.ToString(secret).Replace('-', ' ')) &&
                nativeText.Contains("native association and payload redacted"), "文件不输出LOGIN关联值或Native原始帧");
        }
        finally
        {
            Directory.Delete(root, true);
        }
        Console.WriteLine($"PASS {TestCheck.Count - checks} packet capture assertions");
    }

    static string ReadCapture(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        return reader.ReadToEnd();
    }

    static byte[] ClientFrame(ushort opcode, byte[] body)
    {
        byte[] frame = new byte[13 + body.Length];
        frame[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(3), frame.Length);
        body.CopyTo(frame, 13);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(7), GameFrame.Crc(frame.AsSpan(11)));
        return frame;
    }
}
