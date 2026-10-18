using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class DisjointPackets
{
    public static byte[] RejectedSource(InventoryItem source)
    {
        if (source.Position.List != InventoryListType.Main || source.Core.Kind != DfoServer.GameWorld.Items.ItemKind.Equipment)
            throw new InvalidDataException("分解拒绝收尾只允许原Main装备");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)InventoryListType.Main);
        writer.Write((ushort)2);
        // 同帧按线序先移除旧对象再装入完整核心；不能走会去重同槽的Delta。
        ItemListProtocolWriter.Write(writer, source.Position.Index, null);
        ItemListProtocolWriter.Write(writer, source.Position.Index, source.Core);
        return stream.ToArray();
    }

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
