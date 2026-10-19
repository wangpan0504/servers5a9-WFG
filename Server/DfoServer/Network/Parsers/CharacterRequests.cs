using DfoServer.Game.Characters;
using System.Buffers.Binary;
using System.Text;

namespace DfoServer.Network.Parsers;

internal sealed record CreateCharacterRequest(byte Job, string Name, byte[] Tail);
internal sealed record DeleteCharacterRequest(ushort RosterIndex, string ConfirmationName);
// 重排正文为两个u32持久槽位；客户端名册行携带的slot原样回传，不是UI行索引。
internal sealed record ChangeCharacterSlotRequest(uint SlotA, uint SlotB);
internal static class CharacterRequests
{
    public static void HiddenRoster(ReadOnlySpan<byte> body)
    {
        if (!body.IsEmpty) throw new InvalidDataException("隐藏角色查询正文必须为空");
    }
    public static bool IsRoster(ReadOnlySpan<byte> body) => body.SequenceEqual<byte>([255, 255, 2]);
    public static ushort Select(ReadOnlySpan<byte> body)
    {
        if (body.Length != 2)
            throw new InvalidDataException("Selection body width");
        return BinaryPrimitives.ReadUInt16LittleEndian(body);
    }

    public static string CheckName(ReadOnlySpan<byte> body)
    {
        string name = Name(body, 0);
        if (body.Length != CharacterName.Encoding.GetByteCount(name) + 4)
            throw new InvalidDataException("Name check tail");
        return name;
    }

    public static ChangeCharacterSlotRequest ChangeSlot(ReadOnlySpan<byte> body)
    {
        if (body.Length != 8)
            throw new InvalidDataException("Character slot change body width");
        return new(BinaryPrimitives.ReadUInt32LittleEndian(body), BinaryPrimitives.ReadUInt32LittleEndian(body[4..]));
    }

    public static DeleteCharacterRequest Delete(ReadOnlySpan<byte> body)
    {
        string name = Name(body, 2);
        if (body.Length != 6 + CharacterName.Encoding.GetByteCount(name))
            throw new InvalidDataException("Deletion body tail");
        return new(BinaryPrimitives.ReadUInt16LittleEndian(body), name);
    }

    public static string Name(ReadOnlySpan<byte> body, int at)
    {
        if (body.Length < at + 4)
            throw new InvalidDataException("Name length absent");
        int n = BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
        if (n is < 1 or > 12 || body.Length < at + 4 + n)
            throw new InvalidDataException("Name bounds");
        string name;
        try
        {
            name = CharacterName.Encoding.GetString(body.Slice(at + 4, n));
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Invalid CP936 name", ex);
        }

        if (!CharacterName.IsValid(name))
            throw new InvalidDataException("Invalid name");
        return name;
    }

    public static CreateCharacterRequest ParseCreate(ReadOnlySpan<byte> body)
    {
        string name = Name(body, 1);
        int width = CharacterName.Encoding.GetByteCount(name);
        ReadOnlySpan<byte> tail = body[(5 + width)..];
        // tail[8] 是建角界面当前展示的分支索引；保留请求值，创建业务仍使用基础职业。
        if (body[0] > 15 || tail.Length != 12 || tail[8] > 5 ||
            !tail[..8].SequenceEqual(new byte[] { 0, 0, 0, 0, 0, 0, 255, 0 }) ||
            !tail[9..].SequenceEqual(new byte[] { 0, 0, 0 }))
            throw new InvalidDataException("Unsupported ordinary Client job or creation tail");
        return new(body[0], name, tail.ToArray());
    }
}
