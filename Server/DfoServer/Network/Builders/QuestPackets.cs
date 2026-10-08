using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Network.Builders;

// 这些codec不选择业务错误/成功头或提交奖励；ACK34生产成功入口必须另有完整资格与头字段合同。
internal static class QuestPackets
{
    // 3145合同：clear-map分类3，无材料消耗数组；资源item模式0，职业武器由已提交计划提供。
    // EXP必须来自实际提交增量，当前没有允许零EXP领奖的本服资格规则。
    public static byte[] FinishSingleClearMap(ushort questId, uint committedExperienceDelta,
        IReadOnlyList<QuestRewardItemProjection> rewards)
    {
        int[] armor = [10403, 12403, 14403, 16403, 18403];
        if (questId != 3145 || committedExperienceDelta == 0 || committedExperienceDelta > int.MaxValue ||
            rewards.Count != 7 || rewards[0].Item.Core.ItemId != 10165055 ||
            rewards[1].Item.Core.Kind != ItemKind.Equipment ||
            !rewards.Skip(2).Select(reward => reward.Item.Core.ItemId).SequenceEqual(armor) ||
            rewards.Any(reward => reward.GrantedCount != 1) ||
            rewards.Select(reward => reward.Item.Uid).Distinct().Count() != rewards.Count ||
            rewards.Select(reward => reward.Item.Position).Distinct().Count() != rewards.Count)
            throw new InvalidDataException("3145领奖需要实际正EXP增量与本职业七项固定奖励原序，不支持空奖励或重复资产");
        byte[] rows = FixedRewardRows(rewards);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(questId);
        writer.Write((byte)3);
        writer.Write(committedExperienceDelta);
        writer.Write((byte)0);
        writer.Write(rows);
        return stream.ToArray();
    }

    public static byte[] Rejected(ushort error)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)0);
        writer.Write(error);
        return stream.ToArray();
    }

    public static byte[] ClearTrigger(ushort questId, uint scalar)
    {
        if (questId is 0 or >= 30000)
            throw new InvalidDataException("任务编号超出匹配完成表范围");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(questId);
        writer.Write(scalar);
        return stream.ToArray();
    }

    // 仅为ACK34 mode0的已证奖励数组子段，不含尚未闭合的成功头或mode字节。
    public static byte[] FixedRewardRows(IReadOnlyList<QuestRewardItemProjection> rewards)
    {
        if (rewards.Count > byte.MaxValue)
            throw new InvalidDataException("任务奖励数组超过u8容量");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)rewards.Count);
        foreach (var reward in rewards)
        {
            var item = reward.Item;
            item.Core.Validate();
            if (item.Uid <= 0 || item.Position.Index < 0 || item.Position.List != InventoryListType.Main ||
                item.Core.Kind != ItemKind.Equipment && !item.Core.IsStackable || reward.GrantedCount <= 0 ||
                !item.Core.IsStackable && reward.GrantedCount != 1 || item.Core.IsStackable && reward.GrantedCount > item.Core.Count)
                throw new InvalidDataException("任务奖励投影必须来自实际提交的普通背包资产及本次增量");
            writer.Write(checked((ushort)item.Position.Index));
            writer.Write(checked((uint)item.Core.ItemId));
            writer.Write(checked((uint)(item.Core.IsStackable ? reward.GrantedCount : item.Core.Value)));
            writer.Write(item.Core.AmplifyType);
            writer.Write(item.Core.AmplifyValue);
            writer.Write(unchecked((uint)item.Core.ExpireTime));
            writer.Write(item.Core.SortLockFlag);
            writer.Write(item.Core.RemainUseCount);
        }
        return stream.ToArray();
    }

    // NOTI574清空并恢复全量kind0活动表；调用方不能把单项变化当作全量列表。
    public static byte[] ActiveQuests(IReadOnlyList<ActiveMainQuest> quests)
    {
        if (quests.Count > 30 || quests.Any(quest => quest.Slot > 29 || quest.QuestId is 0 or >= 30000) ||
            quests.Select(quest => quest.Slot).Distinct().Count() != quests.Count ||
            quests.Select(quest => quest.QuestId).Distinct().Count() != quests.Count)
            throw new InvalidDataException("全量活动任务列表存在非法或重复身份");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((uint)quests.Count);
        foreach (var quest in quests.OrderBy(quest => quest.Slot))
        {
            writer.Write(quest.QuestId);
            writer.Write(quest.ConditionState);
        }
        return stream.ToArray();
    }
}

internal sealed record QuestRewardItemProjection(InventoryItem Item, int GrantedCount);
