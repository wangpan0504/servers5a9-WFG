using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace DfoServer.Network.Builders;

internal static class ChannelPackets
{
    public const string ChannelName = "#ch.10";
    const string Key = "EMU-LAB-KEY-0001";
    static void Put(byte[] b, int at, int x) => BitConverter.GetBytes(x).CopyTo(b, at);
    public static byte[] Frame(int op, byte[] body)
    {
        byte[] b = new byte[11 + body.Length];
        b[0] = 0x7c;
        b[1] = (byte)op;
        Put(b, 2, b.Length);
        b[10] = 1;
        body.CopyTo(b, 11);
        return b;
    }

    public static byte[] Hello()
    {
        byte[] b = new byte[36];
        Encoding.ASCII.GetBytes(Key).CopyTo(b, 4);
        return Frame(12, b);
    }

    public static byte[] Script(byte[] script) => Frame(10, Encrypt(script));
    public static byte[] Directory(string address, int port)
    {
        byte[] b = new byte[76];
        Put(b, 0, 1);
        Put(b, 8, 1);
        Encoding.ASCII.GetBytes(ChannelName).CopyTo(b, 12);
        Put(b, 32, 50);
        Encoding.ASCII.GetBytes(address).CopyTo(b, 40);
        Put(b, 72, port);
        return Frame(18, Encrypt(b));
    }

    static byte[] Encrypt(byte[] plain)
    {
        byte[] padded = new byte[(plain.Length + 15) / 16 * 16];
        plain.CopyTo(padded, 0);
        byte[] key = new byte[16];
        Encoding.ASCII.GetBytes(Key).CopyTo(key, 0);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        byte[] encrypted = aes.EncryptEcb(padded, PaddingMode.None);
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, true))
            z.Write(encrypted);
        return output.ToArray();
    }

    public static byte[] Endpoint(string address, int port)
    {
        using var s = new MemoryStream();
        using var w = new BinaryWriter(s);
        w.Write(new byte[] { 1, 1, 1, 0, 1 });
        w.Write(0u);
        byte[] ip = Encoding.ASCII.GetBytes(address);
        w.Write(ip.Length);
        w.Write(ip);
        w.Write(port);
        w.Write(0u);
        w.Write(new byte[] { 0, 0, 1, 1, 0, 0, 0 });
        w.Write(new byte[4]);
        w.Write(0u);
        w.Write(0u);
        w.Write((ushort)0);
        w.Write((byte)0);
        w.Write((byte)0);
        return s.ToArray();
    }
}
