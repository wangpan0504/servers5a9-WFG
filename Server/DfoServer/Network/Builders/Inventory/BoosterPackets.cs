using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class BoosterPackets
{
    public static byte[] Opened(InventoryResult result, BoosterReceipt? receipt)
    {
        if (!result.Accepted)
            return CargoPackets.Ack(result);
        ArgumentNullException.ThrowIfNull(receipt);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write((byte)0); // 成功分支仍读取额外结果码，零表示无需追加错误提示。
        writer.Write(receipt.ItemId);
        writer.Write(receipt.Slot);
        writer.Write(0); // 023708A6读取后未使用的u32，保留完整字段。
        writer.Write(0); // 普通展示模式；1/2另有专属UI处理。
        writer.Write(checked((ushort)receipt.Rewards.Length));
        foreach (var reward in receipt.Rewards)
        {
            writer.Write(reward.ItemId);
            writer.Write(reward.Count);
        }
        return stream.ToArray();
    }
}
