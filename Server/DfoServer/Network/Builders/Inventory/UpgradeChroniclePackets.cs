using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using System.Buffers.Binary;

namespace DfoServer.Network.Builders.Inventory;

internal static class UpgradeChroniclePackets
{
    // 本版 0234BD90：ticket21/23 成功后还读取 result 和消费行数。
    // 本路径只用权威库存投影更新扣费与目标，不再让 ACK 对同一槽重复扣除。
    // state23 不解释 result；保留字段写 0，不冒称未经证实的成功枚举。
    public static byte[] Result(InventoryResult result)
    {
        if (result.Accepted)
            return result.Plan?.Reason == InventoryUpgradeChronicleService.LevelReason ? [1, 0] : [1, 0, 0];
        var body = new byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), result.Error);
        return body;
    }
}
