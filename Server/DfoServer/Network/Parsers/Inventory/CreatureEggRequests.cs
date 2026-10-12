using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class CreatureEggRequests
{
    public static InventorySlot Read(ReadOnlySpan<byte> body)
    {
        // 同一蛋换槽后070000变为070100；只开放这份双槽抓包验证的Pet/u16合同。
        if (body.Length != 3 || body[0] != (byte)InventoryListType.Pet)
            throw new InvalidDataException("宠物蛋孵化请求长度或容器无效");
        ushort slot = BinaryPrimitives.ReadUInt16LittleEndian(body[1..]);
        if (slot > 139)
            throw new InvalidDataException("宠物蛋孵化槽位超出宠物页范围");
        return new(InventoryListType.Pet, (short)slot);
    }
}
