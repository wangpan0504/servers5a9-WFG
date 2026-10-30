using DfoServer.Game.Characters;
using System.Buffers.Binary;
using System.Text;

namespace DfoServer.Network.Parsers;

internal sealed record CreateCharacterRequest(byte Job, string Name, byte[] Tail)
{
    public bool IsPvpPrivate => Tail[6] != 255;
    public byte PvpGrow => Tail[6];
}
internal sealed record DeleteCharacterRequest(ushort RosterIndex, string ConfirmationName);
// 重排正文为两个u32名册行索引（03B45760→03B42550，源0x8f8、目标edi均为绝对列表索引）。
// 有槽位空洞时索引不等于持久槽位，必须按最近已发布名册行序映射。
internal sealed record ChangeCharacterSlotRequest(uint IndexA, uint IndexB);
internal static class CharacterRequests
{
    public static byte Contents(ReadOnlySpan<byte> body)
    {
        // 03B5BB00 → 03BDF720固定写一个u8，是已发布名册行序而非持久slot。
        if (body.Length != 1) throw new InvalidDataException("选角副本次数请求必须是一个名册索引字节");
        return body[0];
    }
    public static void AllServerRoster(ReadOnlySpan<byte> body)
    {
        if (!body.IsEmpty) throw new InvalidDataException("跨大区编队名册请求正文必须为空");
    }

    public static (byte Job,byte Grow) PvpPrivateCheck(ReadOnlySpan<byte> body)
    {
        if(body.Length!=8 || BinaryPrimitives.ReadUInt32LittleEndian(body)>15 || BinaryPrimitives.ReadUInt32LittleEndian(body[4..])>5)
            throw new InvalidDataException("专用角色资格请求必须是合法职业与基础转职两个u32");
        return ((byte)BinaryPrimitives.ReadUInt32LittleEndian(body),(byte)BinaryPrimitives.ReadUInt32LittleEndian(body[4..]));
    }

    public static void HiddenRoster(ReadOnlySpan<byte> body)
    {
        if (!body.IsEmpty) throw new InvalidDataException("隐藏角色查询正文必须为空");
    }
    public static bool IsRoster(ReadOnlySpan<byte> body) => body.SequenceEqual<byte>([255, 255, 2]);
    public static bool TryPeerMenu(ReadOnlySpan<byte> body, out ushort target)
    {
        target = 0;
        // 0399EE90：actor:u16、mode:u8、u32、u16、u32；其他mode有不同长度，不按菜单解读。
        if (body.Length < 3 || body[2] != 15) return false;
        if (body.Length != 13) throw new InvalidDataException("玩家菜单请求长度错误");
        if (BinaryPrimitives.ReadUInt32LittleEndian(body[3..]) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(body[7..]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(body[9..]) != 0) return false;
        target = BinaryPrimitives.ReadUInt16LittleEndian(body);
        return true;
    }
    public static bool TryOtherPlayer(ReadOnlySpan<byte> body, out ushort target)
    {
        target = 0;
        if (body.Length < 3 || body[2] != 3) return false;
        if (body.Length != 3) throw new InvalidDataException("玩家资料请求长度错误");
        target = BinaryPrimitives.ReadUInt16LittleEndian(body);
        return true;
    }
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
        // tail6为FF是普通建角；专用建角tail6/tail8是已证相同基础转职，仍须872会话许可。
        if (body[0] > 15 || tail.Length != 12 || tail[8] > 5 ||
            !tail[..6].SequenceEqual(new byte[6]) || tail[7]!=0 ||
            tail[6]!=255 && (tail[6]>5 || tail[6]!=tail[8]) ||
            !tail[9..].SequenceEqual(new byte[] { 0, 0, 0 }))
            throw new InvalidDataException("Unsupported ordinary Client job or creation tail");
        return new(body[0], name, tail.ToArray());
    }
}
