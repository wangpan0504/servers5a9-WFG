using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Inventory;

internal readonly record struct EpicExchangeRequest(int Slot, int SourceItemId, int TargetItemId);

internal static class EpicExchangeRequests
{
    // 客户端确认窗构包 [来源实例主背包槽位, 源物品编号, 目标物品编号]。
    // 实机抓包：三种来源分别送 18/19/9，与库里 100250656/100130610/101000783 的 Main 槽位逐一对应，
    // 因此首字段不是占位符，按槽位定位实例；长度或尾部不符仍按畸形请求拒绝。
    public static EpicExchangeRequest Exchange(ReadOnlySpan<byte> body)
    {
        if (body.Length != 12)
            throw new InvalidDataException("Client史诗交换请求必须是12字节");
        return new(BinaryPrimitives.ReadInt32LittleEndian(body),
            BinaryPrimitives.ReadInt32LittleEndian(body[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(body[8..]));
    }
}
