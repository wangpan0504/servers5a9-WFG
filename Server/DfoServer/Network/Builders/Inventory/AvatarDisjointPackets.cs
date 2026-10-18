using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class AvatarDisjointPackets
{
    public static byte[] StandardAck(InventoryResult result, AvatarDisjointReceipt? receipt)
    {
        if (!result.Accepted)
            return CargoPackets.Ack(result);
        if (receipt is null)
            throw new InvalidDataException("时装分解成功缺少产物回执");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(receipt.Request.SourceSlot);
        writer.Write(checked((ushort)receipt.Rewards.Length));
        foreach (var reward in receipt.Rewards)
        {
            writer.Write(reward.Slot);
            writer.Write(reward.ItemId);
            writer.Write(reward.Count);
        }
        return stream.ToArray();
    }

    public static byte[] RandomRewardAck(InventoryResult result, AvatarDisjointReceipt? receipt)
    {
        if (!result.Accepted)
            return CargoPackets.Ack(result);
        if (receipt is null)
            throw new InvalidDataException("随机时装分解成功缺少产物回执");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(receipt.Request.SourceSlot);
        writer.Write(checked((byte)receipt.Rewards.Length));
        foreach (var reward in receipt.Rewards)
        {
            writer.Write(reward.ItemId);
            writer.Write(reward.Count);
        }
        return stream.ToArray();
    }
}
