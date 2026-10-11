using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class ChannelTcpTests
{
    public static void Run(TcpTestFixture fixture)
    {
        using (var client = Connect())
        {
            var stream = client.GetStream();
            Write(stream, 11, new byte[32]);
            byte[] hello = Read(stream, 12);
            That(hello.Length == 36, "channel hello width");
            Bytes(hello.AsSpan(4, 16), "EMU-LAB-KEY-0001"u8, "channel key");
            Write(stream, 9, []);
            byte[] script = Decode(Read(stream, 10));
            string path = Path.Combine(AppContext.BaseDirectory, "Resources", "channel.txt");
            using var reference = File.Exists(path) ? File.OpenRead(path) :
                typeof(ChannelTcpTests).Assembly.GetManifestResourceStream("DfoServer.Resources.channel.txt")
                ?? throw new InvalidDataException("频道回归缺少参考脚本");
            using var expectedBytes = new MemoryStream();
            reference.CopyTo(expectedBytes);
            byte[] expected = expectedBytes.ToArray();
            Bytes(script.AsSpan(0, expected.Length), expected, "channel script transport");
            Write(stream, 17, new byte[24]);
            byte[] directory = Decode(Read(stream, 18));
            That(BinaryPrimitives.ReadInt32LittleEndian(directory.AsSpan(72)) == fixture.Options.GamePort && Encoding.ASCII.GetString(directory, 40, 9) == "127.0.0.1", "channel points to isolated game port");
            That(stream.ReadByte() == -1, "channel closes after complete handshake");
        }

        foreach (byte opcode in new byte[]
        {
            9,
            17
        })
        {
            using var client = Connect();
            var stream = client.GetStream();
            Write(stream, opcode, opcode == 9 ? [] : new byte[24]);
            That(stream.ReadByte() == -1, "out-of-order channel request rejected");
        }

        Console.WriteLine("PASS C# TCP channel: hello, encrypted script/directory and handshake order");
        TcpClient Connect()
        {
            var client = new TcpClient();
            try
            {
                client.ConnectAsync("127.0.0.1", fixture.Options.ChannelPort).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                client.GetStream().ReadTimeout = 5000;
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    static void Write(Stream stream, byte opcode, byte[] body)
    {
        byte[] frame = new byte[11 + body.Length];
        frame[1] = opcode;
        frame[10] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(2), frame.Length);
        body.CopyTo(frame, 11);
        stream.Write(frame);
    }

    static byte[] Read(Stream stream, byte opcode)
    {
        byte[] header = new byte[11];
        stream.ReadExactly(header);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(2));
        That(header[0] == 0x7c && header[1] == opcode && header[10] == 1 && length is >= 11 and <= 65536, "channel reply header");
        byte[] body = new byte[length - 11];
        stream.ReadExactly(body);
        return body;
    }

    static byte[] Decode(byte[] body)
    {
        using var compressed = new MemoryStream(body);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var encrypted = new MemoryStream();
        zlib.CopyTo(encrypted);
        using var aes = Aes.Create();
        aes.Key = "EMU-LAB-KEY-0001"u8.ToArray();
        return aes.DecryptEcb(encrypted.ToArray(), PaddingMode.None);
    }
}
