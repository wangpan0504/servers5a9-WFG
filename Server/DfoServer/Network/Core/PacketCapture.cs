using System.Buffers.Binary;
using System.Text;
using DfoServer.Infrastructure;

namespace DfoServer.Network;

// 只记录完整应用层帧；关闭时不创建实例或文件，写入失败不终止游戏连接。
internal sealed class PacketCapture : IDisposable
{
    readonly BufferedLogFile file;
    int failed;
    public bool IsEnabled => Volatile.Read(ref failed) == 0;

    public PacketCapture(string directory)
    {
        file = new(Path.Combine(directory, "packet_log.txt"), Disable);
        Append($"=== Packet capture {DateTimeOffset.Now:O}; GAME SEND=16B, RECV=13B; CHANNEL=11B ===\n");
    }

    public void Write(int connection, bool channel, bool send, ReadOnlySpan<byte> frame)
    {
        if (Volatile.Read(ref failed) != 0 || frame.Length < (channel ? 2 : 3))
            return;
        int header = channel ? 11 : send ? 16 : 13;
        ushort opcode = channel ? frame[1] : BinaryPrimitives.ReadUInt16LittleEndian(frame[1..]);
        string name = channel ? "CHANNEL" : frame[0] == 1 ? ((CmdPacketType)opcode).ToString() : ((NotiPacketType)opcode).ToString();
        var text = new StringBuilder();
        text.Append($"{DateTimeOffset.Now:O} connection={connection} protocol={(channel ? "CHANNEL" : "GAME")} {(send ? "SEND" : "RECV")} kind=0x{frame[0]:X2} opcode=0x{opcode:X4} name={name} len={frame.Length} body=({Math.Max(0, frame.Length - header)}B)");
        // 连同畸形认证包一起脱敏；旧扩展包也可能含凭据，不输出 body 或 raw。
        bool redact = !channel && !send && IsAuthentication(opcode);
        if (redact)
            text.Append(" [authentication payload redacted]\n");
        else
        {
            text.Append(" [");
            Hex(text, frame[Math.Min(header, frame.Length)..]);
            text.Append("]\n  raw: ");
            Hex(text, frame);
            text.Append('\n');
        }
        Append(text.ToString());
    }

    internal static bool IsAuthentication(ushort opcode) => opcode is
        (ushort)CmdPacketType.LOGIN or (ushort)CmdPacketType.LOGIN_PRECHECK or (ushort)CmdPacketType.CLIENT_SPEC_STATISTIC;

    static void Hex(StringBuilder text, ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i != 0)
                text.Append(' ');
            text.Append(bytes[i].ToString("X2"));
        }
    }

    void Append(string text)
    {
        if (Volatile.Read(ref failed) != 0) return;
        try { file.Append(text); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Disable(error); }
    }
    void Disable(Exception error)
    {
        if (Interlocked.Exchange(ref failed, 1) == 0)
            Console.Error.WriteLine("Packet capture disabled: " + error.Message);
    }
    public void Flush()
    {
        try { file.Flush(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Disable(error); }
    }
    public void Dispose()
    {
        try { file.Dispose(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Disable(error); }
        finally { Interlocked.Exchange(ref failed, 1); }
    }
}
