using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Parsers.Inventory;

namespace DfoServer.Network.Builders.Inventory;

internal static class CharmChargingPackets
{
    public static byte[] Result(CharmChargingRequest request, InventoryResult result)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        // 01B3B500仅在成功信封中读取16B业务结果；4为满能量，其余非零为无效充能目标。
        writer.Write((byte)1);
        writer.Write(result.Accepted ? 0u : result.Error == 4 ? 4u : 1u);
        writer.Write((int)request.Target.Index);
        writer.Write(0u); // 本版复制后未消费的第三个DWORD。
        int restored = result.Accepted && result.Receipt is { } receipt && result.Plan is { } plan
            ? receipt.Index.Find(request.Target)!.Core.Durability - plan.BeforeIndex.Find(request.Target)!.Core.Durability : 0;
        writer.Write(restored);
        return stream.ToArray();
    }
}
