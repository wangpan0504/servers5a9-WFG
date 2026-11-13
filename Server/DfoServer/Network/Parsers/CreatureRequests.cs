using System.Buffers.Binary;
using System.Text;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers;

internal sealed record CreatureRenameRequest(InventorySlot Card, string Name);
internal sealed record CreatureMessageRequest(byte Type, ushort Target, int Reference, string Text);

internal static class CreatureRequests
{
    public static CreatureRenameRequest Rename(ReadOnlySpan<byte> body)
    {
        if (body.Length < 7) throw new InvalidDataException("宠物改名正文过短");
        return new(new((InventoryListType)body[2], BinaryPrimitives.ReadInt16LittleEndian(body)), Text(body[3..], 19));
    }

    public static ushort Response(ReadOnlySpan<byte> body) => body.Length == 2
        ? BinaryPrimitives.ReadUInt16LittleEndian(body) : throw new InvalidDataException("宠物互动正文必须为u16引用");

    public static CreatureMessageRequest Message(ReadOnlySpan<byte> body)
    {
        if (body.Length < 11) throw new InvalidDataException("宠物喊话正文过短");
        return new(body[0], BinaryPrimitives.ReadUInt16LittleEndian(body[1..]), BinaryPrimitives.ReadInt32LittleEndian(body[3..]),
            Text(body[7..], 255));
    }

    public static InventorySlot EvolutionOrBookSlot(ReadOnlySpan<byte> body, InventoryListType defaultList = InventoryListType.Pet) =>
        body.Length switch
        {
            2 => new(defaultList, BinaryPrimitives.ReadInt16LittleEndian(body)),
            3 => new((InventoryListType)body[2], BinaryPrimitives.ReadInt16LittleEndian(body)),
            >= 6 => new((InventoryListType)body[2], BinaryPrimitives.ReadInt16LittleEndian(body)),
            _ => throw new InvalidDataException("宠物道具槽位请求长度无效")
        };

    // 02686369：CMD 555 (CREATURE_FILLPOINT) 写入 4B 点数 (points > 0)。
    public static int FillPoint(ReadOnlySpan<byte> body)
    {
        if (body.Length != 4) throw new InvalidDataException("宠物充能点数请求必须为4字节");
        return BinaryPrimitives.ReadInt32LittleEndian(body);
    }

    static string Text(ReadOnlySpan<byte> body, int limit)
    {
        if (body.Length < 4) throw new InvalidDataException("宠物文本缺少长度");
        int length = BinaryPrimitives.ReadInt32LittleEndian(body);
        if (length < 0 || length > limit || body.Length != length + 4 || body[4..].Contains((byte)0))
            throw new InvalidDataException("宠物文本长度或内容无效");
        try { return CharacterName.Encoding.GetString(body[4..]); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("宠物文本代码页无效", error); }
    }
}
