using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class ChannelTcpTests
{
    public static void Run(TcpTestFixture fixture, string serverAddress = "127.0.0.1")
    {
        var endpoints = Endpoints(fixture);
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
            using var reader = new StreamReader(reference, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            byte[] expected = Encoding.GetEncoding(936).GetBytes(reader.ReadToEnd());
            Bytes(script.AsSpan(0, expected.Length), expected, "channel script transport");
            string decoded = Encoding.GetEncoding(936).GetString(script).TrimEnd('\0');
            That(Regex.IsMatch(decoded, @"\[server\]\s+2\s"), "下发脚本属于服务器2，不依赖PVF中预存server0");
            (string Group, int Type)[] groups =
            [
                ("elven_guard", 22), ("granfloris", 22), ("sky_catle", 22), ("behemoth", 22),
                ("Alfhlyra", 22), ("Noirpera", 22), ("Charnak", 22), ("Abnoba", 22),
                ("Meltdown", 22), ("FallsToTheSky", 22), ("raid", 23), ("luke_raid", 32),
                ("metro", 22), ("north_myre", 22), ("deathtower", 11), ("ancient", 11),
                ("Antwer", 22), ("impossible", 11), ("seatrain", 22), ("stormpass", 22),
                ("timedoor", 22), ("powerstation", 22), ("attackzone", 18), ("trade", 3),
                ("noblsky", 22), ("CastleofDead", 22), ("crack", 11), ("sainthorn", 26),
                ("harlem", 22), ("east harlem", 22), ("fiendwar", 45), ("pvp", 24), ("evildom_war", 54), ("prey", 50), ("xian_village", 56), ("dnfaos_free", 63), ("dnfaos_rank", 62), ("evildom_gathering", 53)
            ];
            foreach (var (group, type) in groups)
                That(Regex.IsMatch(decoded, @"(?m)^\d+ `<(?:\d+::)?[A-Za-z0-9_]+>` " + type + @" `\[" + Regex.Escape(group) + @"\]` "),
                    "普通区域使用本版可入表的type22，特殊分类保持原类型 " + group);
            Write(stream, 17, new byte[24]);
            byte[] directory = Decode(Read(stream, 18));
            That(BinaryPrimitives.ReadInt32LittleEndian(directory) == 1 &&
                BinaryPrimitives.ReadInt32LittleEndian(directory.AsSpan(4)) == 2 &&
                BinaryPrimitives.ReadInt32LittleEndian(directory.AsSpan(8)) == 38, "目录声明服务器2和全部38个入口");
            for (int i = 0; i < 38; i++)
            {
                int at = 12 + i * 64;
                int number = i == 0 ? 1 : i + 9;
                That(Encoding.ASCII.GetString(directory, at, 20).TrimEnd('\0') == "#ch." + number, "目录保留独立分类号 " + number);
                That(BinaryPrimitives.ReadInt32LittleEndian(directory.AsSpan(at + 60)) == endpoints[(byte)number] &&
                    Encoding.ASCII.GetString(directory, at + 28, 32).TrimEnd('\0') == serverAddress, "分类入口端口独立，同一目录使用本连接的服务端地址");
            }
            That(stream.ReadByte() == -1, "channel closes after complete handshake");
        }

        using (var cached = Connect())
        {
            var stream = cached.GetStream();
            Write(stream, 11, new byte[32]); Read(stream, 12);
            Write(stream, 17, new byte[24]);
            var directory = Decode(Read(stream, 18));
            That(BinaryPrimitives.ReadInt32LittleEndian(directory.AsSpan(8)) == 38 && stream.ReadByte() == -1,
                "已缓存脚本的11/17自动重连只收到目录并结束，不等待9或补发未请求脚本");
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
                client.ConnectAsync(serverAddress, fixture.Options.ChannelPort).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
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

    public static Dictionary<byte, int> Endpoints(TcpTestFixture fixture)
    {
        using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Options.LogDirectory, "ready.json")));
        var entries = ready.RootElement.GetProperty("ChannelEntries").EnumerateArray().ToDictionary(
            row => row.GetProperty("Entry").GetProperty("Identity").GetProperty("Number").GetByte(),
            row => row.GetProperty("Port").GetInt32());
        That(entries.Count == 38 && entries.Values.Distinct().Count() == 38 && entries[10] == fixture.Options.GamePort &&
            entries.Values.All(port => port is > 0 and <= 65535 && port != fixture.Options.ChannelPort && port != fixture.Options.GmPort),
            "一个服务实例提供38个不同入口，不占用频道或GM端口");
        return entries;
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
