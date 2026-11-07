using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal readonly record struct CharmChargingRequest(InventorySlot Target, InventorySlot Source);

internal static class CharmChargingRequests
{
    public static bool TryRead(ReadOnlySpan<byte> body, out CharmChargingRequest request)
    {
        request = default;
        // 01B3B7C0发送21B，前13B未赋业务值；只读取实际写入的两个Main槽，不校验栈上尾料。
        if (body.Length != 21) return false;
        int target = BinaryPrimitives.ReadInt32LittleEndian(body[13..]);
        int source = BinaryPrimitives.ReadInt32LittleEndian(body[17..]);
        if (target is < 0 or > short.MaxValue || source is < 0 or > short.MaxValue) return false;
        request = new(new(InventoryListType.Main, (short)target), new(InventoryListType.Main, (short)source));
        return true;
    }
}
