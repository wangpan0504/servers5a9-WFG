using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.TagEvildom;

namespace DfoServer.Network.Builders;

internal readonly record struct TagEvildomRewardRow(uint ItemId, uint Count);
// 仅展示合同，不是发奖授权；EG_Reward.clear_time_draw把真实耗时毫秒格式化为分、秒及百分秒。
internal sealed record TagEvildomClearPresentation(ImmutableArray<TagEvildomRewardRow> Rows, long ClearElapsedMilliseconds);

internal static class TagEvildomRunPackets
{
    public static byte[] SwitchAcknowledgement(TagEvildomLifeResult committed)
    {
        if (committed.Operation != TagEvildomLifeOperation.Switch || committed.Outcome is not (DungeonRunOutcome.Accepted or DungeonRunOutcome.Duplicate) ||
            committed.Snapshot.Defeated || committed.Snapshot.Cancelled || committed.DefeatAcknowledgement || committed.Revived is not null)
            throw new InvalidDataException("切角成功必须来自合法生命状态提交");
        return [1];
    }

    public static byte[] DefeatAcknowledgement(TagEvildomLifeResult committed)
    {
        if (committed.Operation != TagEvildomLifeOperation.Death || committed.Outcome != DungeonRunOutcome.Accepted || !committed.DefeatAcknowledgement ||
            !committed.Snapshot.Defeated || committed.Snapshot.Cancelled || committed.Snapshot.Slots.Any(slot => slot.Alive))
            throw new InvalidDataException("1469成功会触发败北回城，只能确认真实双槽已死");
        return [1];
    }

    public static byte[] SwitchNotification(TagEvildomLifeResult committed)
    {
        if (committed.Outcome != DungeonRunOutcome.Accepted || committed.SwitchTo is not { } ordinal || ordinal > 1 ||
            committed.Snapshot.Cancelled || committed.Snapshot.ActiveOrdinal != ordinal || !committed.Snapshot.Slots[ordinal].Alive)
            throw new InvalidDataException("1407必须对应本次实际切入的活槽");
        return [ordinal];
    }

    public static byte[] Revival(TagEvildomLifeResult committed)
    {
        if (committed.Operation != TagEvildomLifeOperation.Revival || committed.Outcome != DungeonRunOutcome.Accepted || committed.Revived is not { } ordinal || ordinal > 1 ||
            committed.Snapshot.Cancelled || !committed.Snapshot.Slots[ordinal].Alive)
            throw new InvalidDataException("1676必须对应本次实际复活的槽位");
        var body = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, ordinal);
        return body;
    }

    // 仅定义公共失败结构，不在这里猜测未闭合业务分支应使用的错误码。
    public static byte[] Failure(ushort error) => [0, (byte)error, (byte)(error >> 8)];

    public static byte[] ClearPresentation(TagEvildomClearPresentation presentation)
    {
        if (presentation.Rows.IsDefault || presentation.Rows.Length > 10 || presentation.ClearElapsedMilliseconds < 0 ||
            presentation.Rows.Any(row => row.ItemId == 0 || row.Count == 0))
            throw new InvalidDataException("1679结果条目或显示时间超出已核消费者边界");
        var body = new byte[92];
        for (int index = 0; index < presentation.Rows.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(index * 8), presentation.Rows[index].ItemId);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(index * 8 + 4), presentation.Rows[index].Count);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(80), checked((uint)presentation.Rows.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(84), checked((uint)Math.Min(presentation.ClearElapsedMilliseconds, 3_600_000)));
        // 末4B沿017851C3构造初值0；已核展示路径不消费，不能赋予收件人或次数语义。
        return body;
    }
}
