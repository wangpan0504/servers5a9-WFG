using System.Buffers.Binary;
using DfoServer.Game.Party;
using DfoServer.Game.Characters;
using System.Text;

namespace DfoServer.Network.Parsers;

internal sealed record PartyDirectoryQuery(ushort Actor, ushort Page, byte Sort, bool Reverse,
    bool SameChannel, bool Recruiting, byte Kind, byte Options, string Keyword);

internal static class PartyRequests
{
    public static PartyDirectoryQuery Directory(ReadOnlySpan<byte> body)
    {
        // 01463630/014D5D10：18B路由头后为40B查询，不能当作任意peer转发。
        if (body.Length != 58 || body[2] != 1) throw new InvalidDataException("队伍查询长度或子型错误");
        if (body[21] > 1 || body[22] > 1 || body[23] > 1)
            throw new InvalidDataException("队伍查询布尔字段错误");
        var keyword = body.Slice(26, 32);
        int end = keyword.IndexOf((byte)0);
        if (end < 0) throw new InvalidDataException("队伍查询字符串缺少结束符");
        string text;
        try { text = CharacterName.Encoding.GetString(keyword[..end]); }
        catch (DecoderFallbackException e) { throw new InvalidDataException("队伍查询字符串不是CP936", e); }
        if (text.Any(char.IsControl)) throw new InvalidDataException("队伍查询字符串包含控制字符");
        return new(BinaryPrimitives.ReadUInt16LittleEndian(body[15..]), BinaryPrimitives.ReadUInt16LittleEndian(body[18..]),
            body[20], body[21] != 0, body[22] != 0, body[23] != 0, body[24], body[25], text);
    }

    public static (byte Operation, PartySettings Settings) Information(ReadOnlySpan<byte> body)
    {
        var reader = new SocialRequests.Reader(body);
        byte operation = reader.Byte(), titleCode = reader.Byte();
        string title = titleCode == 0 ? reader.String(63) : "";
        byte capacity = reader.Byte();
        // S5使用u32副本号，不能套用S4的12字节设置块。
        uint dungeon = (uint)(reader.Word() | reader.Word() << 16);
        var settings = new PartySettings(titleCode, title, capacity, dungeon, reader.Byte(), reader.Word(),
            reader.Byte(), reader.Byte(), reader.Word(), null);
        // 02D95D58的operation1/kind1快捷改名没有末尾选项；普通创建/编辑仍完整读取。
        if (!(reader.Empty && operation == 1 && settings.Kind == 1)) settings = settings with { Option = reader.Byte() };
        reader.End();
        return (operation, settings);
    }

    public static PartyEndpoint Endpoint(byte[] body)
    {
        // 实收31B：15B端点头 + u32长度 + 12B机器标识。标识不用于玩家寻址。
        if (body.Length < 15 || body.Length > 147 || body.Length != 15 &&
            (body.Length < 19 || BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(15)) != body.Length - 19))
            throw new InvalidDataException("UDP端点长度错误");
        uint inner = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(1));
        uint outer = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(5));
        ushort port = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(9));
        uint mtu = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(11));
        static bool Unicast(uint ip) => (byte)ip is > 0 and < 224 && ip != uint.MaxValue;
        if (!Unicast(inner) || !Unicast(outer) || port == 0 || mtu is < 256 or > 65507)
            throw new InvalidDataException("UDP端点字段错误");
        return new(body[0], inner, outer, port, mtu);
    }
}
