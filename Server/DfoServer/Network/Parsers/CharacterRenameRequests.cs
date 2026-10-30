using System.Buffers.Binary;
using DfoServer.Game.Characters;

namespace DfoServer.Network.Parsers;

internal sealed record CharacterRenameRequest(byte Index, string OldName, string NewName, byte Reason);

internal static class CharacterRenameRequests
{
    public static ushort Use(ReadOnlySpan<byte> body)
    {
        if (body.Length != 2) throw new InvalidDataException("角色更名卡正文必须是Main槽u16");
        return BinaryPrimitives.ReadUInt16LittleEndian(body);
    }

    public static CharacterRenameRequest Complete(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty) throw new InvalidDataException("角色改名缺名册行序");
        int at = 1;
        string oldName = ReadName(body, ref at);
        string newName = ReadName(body, ref at);
        if (body.Length != at + 1) throw new InvalidDataException("角色改名原因或尾部无效");
        return new(body[0], oldName, newName, body[at]);
    }

    static string ReadName(ReadOnlySpan<byte> body, ref int at)
    {
        if (body.Length < at + 4) throw new InvalidDataException("角色改名缺名称长度");
        int count = BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
        at += 4;
        if (count is < 0 or > 128 || body.Length < at + count) throw new InvalidDataException("角色改名名称长度无效");
        string name = CharacterName.Encoding.GetString(body.Slice(at, count));
        at += count;
        return name;
    }
}
