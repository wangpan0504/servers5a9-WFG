using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal sealed record GuildNpcRequest(uint Action, uint NpcId, byte[] Arguments)
{
    public uint Argument => BinaryPrimitives.ReadUInt32LittleEndian(Arguments);
    public uint[] Training => Enumerable.Range(0, 12)
        .Select(at => BinaryPrimitives.ReadUInt32LittleEndian(Arguments.AsSpan(at * 4))).TakeWhile(id => id != 0).ToArray();
}
internal static class GuildNpcRequests
{
    public static bool TryParse(ReadOnlySpan<byte> body, out GuildNpcRequest? request)
    {
        request = null;
        // 005213E0把含13字节旧结构前缀的69字节内存块写入正文；动作与NPC从13/17开始。
        // 前缀不是权威身份，业务只使用当前账号owner、选角epoch与持久会籍。
        if (body.Length != 69) return false;
        uint action = BinaryPrimitives.ReadUInt32LittleEndian(body[13..]);
        if (action is < 1 or > 6) return false;
        if (action == 3 && (BinaryPrimitives.ReadUInt32LittleEndian(body[21..]) == 0 ||
            body[25..].IndexOfAnyExcept((byte)0) >= 0)) return false;
        if (action == 6)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(body[17..]) != 0) return false;
            bool ended = false; int count = 0;
            var ids = new HashSet<uint>();
            for (int at = 21; at < 69; at += 4)
            {
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(body[at..]);
                if (id == 0) { ended = true; continue; }
                if (ended || ++count > 4 || !ids.Add(id)) return false;
            }
        }
        if (action is 4 or 5 && (BinaryPrimitives.ReadUInt32LittleEndian(body[17..]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(body[21..]) == 0 || body[25..].IndexOfAnyExcept((byte)0) >= 0)) return false;
        request = new(action, BinaryPrimitives.ReadUInt32LittleEndian(body[17..]), body[21..].ToArray());
        return true;
    }
}
