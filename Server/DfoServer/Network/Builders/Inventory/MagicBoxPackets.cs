using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class MagicBoxPackets
{
    // 随机盒结果窗复用当前物品列表读取器的完整条目宽度。该读取器在
    // ARAD.exe 中按 123 字节步进；使用旧的 0x77(119) 会导致第二行
    // 从数量字段中间开始读取，表现为“猫头鹰/金币 0 个”和双倍栏空白。
    const int RewardEntrySize = ItemListProtocolWriter.EntrySize;

    public static byte[] Failure() => [0];

    public static byte[] Opened(MagicBoxReceipt receipt) => OpenedSingle(receipt);

    public static byte[] OpenedSingle(MagicBoxReceipt receipt)
    {
        // 赛丽亚单开沿用客户端单列表读取器；触发双倍时需把基础奖励和双倍奖励分别写入连续条目，避免结果窗停留在加载状态。
        var rewards = receipt.DoubledRewards.Count == 0
            ? VisibleRewards(receipt.DisplayRewards)
            : VisibleRewards(receipt.DisplayRewards.Concat(receipt.DoubledRewards).ToArray());
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(receipt.RawListType);
        writer.Write((byte)(receipt.DoubledRewards.Count == 0 ? 0 : 1));
        writer.Write(receipt.BoxSlot);
        writer.Write(receipt.MaterialSlot);
        writer.Write(checked((short)rewards.Count));
        foreach (var reward in rewards)
            WriteReward(writer, reward);
        return stream.ToArray();
    }

    public static byte[] OpenedExpand(MagicBoxReceipt receipt)
    {
        var rewards = VisibleRewards(receipt.Rewards);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write((byte)4);
        writer.Write(checked((short)receipt.OpenCount));
        writer.Write(receipt.BoxSlot);
        writer.Write(receipt.MaterialSlot);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write(checked((short)rewards.Count));
        foreach (var reward in rewards)
            WriteReward(writer, reward);
        return stream.ToArray();
    }

    public static byte[] OpenedSeriaExpand(MagicBoxReceipt receipt)
    {
        var rewards = VisibleRewards(receipt.DisplayRewards);
        var doubleRewards = VisibleRewards(receipt.DoubledRewards);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)4);
        writer.Write((byte)4);
        writer.Write((byte)(doubleRewards.Count == 0 ? 0 : 1));
        writer.Write(checked((short)receipt.OpenCount));
        writer.Write(receipt.BoxSlot);
        writer.Write(receipt.MaterialSlot);
        writer.Write(checked((short)rewards.Count));
        foreach (var reward in rewards)
            WriteReward(writer, reward);
        writer.Write((short)0); // 第二栏前的分隔计数。
        writer.Write(checked((short)doubleRewards.Count));
        foreach (var reward in doubleRewards)
            WriteReward(writer, reward);
        return stream.ToArray();
    }

    static void WriteReward(BinaryWriter writer, MagicBoxGrantedItem reward)
    {
        long rowStart = writer.BaseStream.Position;
        writer.Write(reward.Slot);
        writer.Write(reward.ItemId);
        writer.Write(reward.Equipment ? 999_999_998 : reward.Count);
        writer.Write((byte)0);
        writer.Write(reward.Durability);
        while (writer.BaseStream.Position - rowStart < RewardEntrySize)
            writer.Write((byte)0);
    }

    // 直接生效的奖励没有背包行，结果读取器不能接收 -1 槽位，否则成功发奖后仍可能断开；虚拟货币保留有效显示槽位。
    static IReadOnlyList<MagicBoxGrantedItem> VisibleRewards(IReadOnlyList<MagicBoxGrantedItem> rewards) =>
        rewards.Where(reward => reward.Slot >= 0).ToArray();

    // NOTI86 (SERVER_BROADCAST) subType 18：客户端 023C1B33 读取角色名、随机盒ID、奖励物品ID并输出 dstr 70435 公告。
    public static byte[] Broadcast(string characterName, int boxItemId, int rewardItemId)
    {
        byte[] name = DfoServer.Game.Characters.CharacterName.Encoding.GetBytes(characterName);
        using var stream = new MemoryStream(1 + 1 + name.Length + 8);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)18);
        writer.Write(checked((byte)name.Length));
        writer.Write(name);
        writer.Write(boxItemId);
        writer.Write(rewardItemId);
        return stream.ToArray();
    }
}
