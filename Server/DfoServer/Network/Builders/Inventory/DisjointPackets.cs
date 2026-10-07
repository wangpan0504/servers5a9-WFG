using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class DisjointPackets
{
    public static byte[] Ack(InventoryResult result, DisjointReceipt? receipt)
    {
        if (!result.Accepted)
            return CargoPackets.Ack(result);
        if (receipt is null)
            throw new InvalidDataException("分解成功必须有已规划产物");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(receipt.Request.Position.Index);
        writer.Write((byte)receipt.Request.Position.List);
        // 0236CF53额外读取u16并与本地角色ID比较；NPC/便携入口没有玩家分解机owner。
        writer.Write(ushort.MaxValue);
        writer.Write(checked((byte)receipt.Rewards.Length));
        foreach (var reward in receipt.Rewards)
        {
            writer.Write(reward.Slot);
            writer.Write(reward.ItemId);
            writer.Write(reward.Count); // 0236D1FD增加本次数量，不能填最终堆叠总量。
        }
        return stream.ToArray();
    }
}
