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
            using var faultBytes = new MemoryStream();
            var faultConnection = new ConnectionContext(14, IPAddress.Loopback, faultBytes, stop, capture: failedCapture);
            await faultConnection.WriteAsync(sent);
            TestCheck.Bytes(faultBytes.ToArray(), sent, "capture I/O failure does not close or change transport");
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
