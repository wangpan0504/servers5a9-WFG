using System.Buffers.Binary;
using System.Text;
using DfoServer.Game.Characters;
using DfoServer.Game.Parties;

namespace DfoServer.Network.Parsers;

internal static class PartyRequests
{
    // 037E7160/01B4B620：本版比旧参考项目多一个末尾u8，不能接受旧14B布局。
    public static SoloPartyRequest Set(ReadOnlySpan<byte> body)
    {
        if (body.Length < 15) throw new InvalidDataException("队伍设置缺少完整字段");
        int at = 2;
        string? title = null;
        if (body[1] == 0)
        {
            if (body.Length < 19) throw new InvalidDataException("队伍名称缺少长度");
            int length = BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
            at += 4;
            if (length is < 1 or > 63 || body.Length != at + length + 13)
                throw new InvalidDataException("队伍名称或尾部长度不符");
            try { title = CharacterName.Encoding.GetString(body.Slice(at, length)); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("队伍名称不是有效CP936", error); }
            if (title.Any(char.IsControl) || string.IsNullOrWhiteSpace(title)) throw new InvalidDataException("队伍名称无效");
            at += length;
        }
        if (body.Length != at + 13) throw new InvalidDataException("队伍设置包含未知尾部");
        return new(body[0], new(body[1], title, body[at], BinaryPrimitives.ReadUInt32LittleEndian(body[(at + 1)..]),
            body[at + 5], BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 6)..]), body[at + 8], body[at + 9],
            BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 10)..]), body[at + 12]));
    }

    public static void Leave(ReadOnlySpan<byte> body)
    {
        if (!body.IsEmpty) throw new InvalidDataException("单人退队正文必须为空");
    }
}
