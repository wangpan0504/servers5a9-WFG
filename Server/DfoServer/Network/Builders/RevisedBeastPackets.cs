using DfoServer.Game.Dungeons;
namespace DfoServer.Network.Builders;

internal static class RevisedBeastPackets
{
    // 安妮退场走命令39的原生消费者，末两字节是当前实例对象键。
    public static byte[] RetireAnne(ushort objectKey) => [0, 0, 0, (byte)objectKey, (byte)(objectKey >> 8)];

    // NOTI1410 00CD2830：u32字段（本版下游未使用）、u8条数、逐条u32物品/u32数量。
    // 非空列表经00CD2670打开0x645抽屉；仅展示已经提交的阶段奖励。
    public static byte[] StageReward(RevisedBeastStageReceipt receipt)
    {
        using var stream=new MemoryStream(); using var writer=new BinaryWriter(stream);
        writer.Write(0u); writer.Write(checked((byte)receipt.Commit.Rewards.Length));
        foreach(var reward in receipt.Commit.Rewards)
        { writer.Write(checked((uint)reward.ItemId)); writer.Write(checked((uint)reward.Count)); }
        return stream.ToArray();
    }

    // NOTI1409首u32为耗时秒，00CD1FA4存入结果+60，00B0C51A除60显示分秒。
    // 成功时每人追加积分及至多三条个人纪录（积分/耗时秒），00B0C4F0读取+5C与+4C显示。
    public static byte[] Result(RevisedBeastSettlementReceipt receipt)
    {
        using var stream=new MemoryStream(); using var writer=new BinaryWriter(stream);
        writer.Write(receipt.ElapsedSeconds); writer.Write(receipt.Terminal.Success?(byte)1:(byte)0); writer.Write((byte)1);
        writer.Write(receipt.Scene.PartySlot); writer.Write(checked((byte)receipt.DisplayRewards.Length));
        foreach(var reward in receipt.DisplayRewards) { writer.Write(checked((uint)reward.ItemId));writer.Write(checked((uint)reward.Count)); }
        if(receipt.Terminal.Success)
        {
            writer.Write(receipt.Points); writer.Write(checked((byte)receipt.BestRecords.Length));
            foreach(var record in receipt.BestRecords) { writer.Write(record.Points); writer.Write(record.ElapsedSeconds); }
        }
        return stream.ToArray();
    }
}
