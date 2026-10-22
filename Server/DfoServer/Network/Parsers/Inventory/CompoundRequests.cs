using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Inventory;

// CMD25 COMPOUND_ITEM 请求：7B 或 9B，两种宽度都已由实机与参考实现对照确认。
//   raw   = u32LE(body[0..4])   源：mode=0 时是 Main 槽位，mode=1 时是物品编号
//   mode  = body[4]             0=按槽位取设计图，1=按物品编号走商品菜单
//   count = u16LE(body[5..7])   请求数量，必须非零
//   9B 型额外要求 body[7]=0x00、body[8]=0x01，其它尾巴视为结构错误
internal readonly record struct CompoundRequest(uint Raw, byte Mode, ushort Count);

internal static class CompoundRequests
{
    public const byte SourceIsSlot = 0;
    public const byte SourceIsItemId = 1;

    public static CompoundRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length is not (7 or 9))
            throw new InvalidDataException("制造请求宽度必须是7或9字节");
        if (body.Length == 9 && (body[7] != 0x00 || body[8] != 0x01))
            throw new InvalidDataException("制造请求9字节尾部必须是0001");
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(body[5..]);
        if (count == 0)
            throw new InvalidDataException("制造请求数量不能为零");
        byte mode = body[4];
        if (mode is not (SourceIsSlot or SourceIsItemId))
            throw new InvalidDataException("制造请求来源方式必须是0或1");
        return new(BinaryPrimitives.ReadUInt32LittleEndian(body), mode, count);
    }
}
