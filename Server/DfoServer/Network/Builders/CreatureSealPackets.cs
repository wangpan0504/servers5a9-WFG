using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Parsers;

namespace DfoServer.Network.Builders;

internal static class CreatureSealPackets
{
    public static byte[] Ack(CreatureSealRequest request, InventoryResult result)
    {
        if (!result.Accepted) return [0, (byte)result.Error, (byte)(result.Error >> 8)];
        var after = result.Receipt?.Character.Inventory ?? result.Plan?.After ??
            throw new InvalidOperationException("宠物封印成功回执缺少库存");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        // 023532F0成功读取14B；状态0、未消费的2B、宠物槽、工具槽和工具剩余数量。
        writer.Write(0);
        writer.Write((short)0);
        writer.Write(request.PetSlot);
        writer.Write(request.SourceSlot);
        writer.Write(after.Find(new(InventoryListType.Main, request.SourceSlot)) is { } remaining &&
            remaining.Core.ItemId is 2683240 or 2683241 or 2683804 ? remaining.Core.Count : 0);
        return stream.ToArray();
    }
}
