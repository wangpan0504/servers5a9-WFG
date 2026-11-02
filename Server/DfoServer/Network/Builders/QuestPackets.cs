using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Quests;
using DfoServer.Network.Builders.Inventory;

namespace DfoServer.Network.Builders;

// 这些codec不选择业务错误/成功头或提交奖励；ACK34生产成功入口必须另有完整资格与头字段合同。
internal static class QuestPackets
{
    public static byte[] AvailableList(byte level, IReadOnlyList<int> ids)
    {
        if (level is < 1 or > 95 || ids.Count > 29999 || ids.Any(id => id is <= 0 or >= 30000) || ids.Distinct().Count() != ids.Count)
            throw new InvalidDataException("可见任务列表的等级或编号无效");
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)8); Varint(writer, 21);
            // Full03 field2为required，构造缺省0；通知消费者不读取其值，语义暂不命名。
            writer.Write((byte)16); Varint(writer, 0);
            writer.Write((byte)24); Varint(writer, level);
            if (ids.Count != 0)
            {
                using var packed = new MemoryStream();
                using (var values = new BinaryWriter(packed, System.Text.Encoding.UTF8, leaveOpen: true))
                    foreach (int id in ids) Varint(values, checked((uint)id));
                writer.Write((byte)34); Varint(writer, checked((uint)packed.Length)); writer.Write(packed.ToArray());
            }
        }
        using var bytes = new MemoryStream();
        using var output = new BinaryWriter(bytes);
        output.Write(checked((uint)payload.Length)); output.Write(payload.ToArray());
        return bytes.ToArray();
    }

    private static void Varint(BinaryWriter writer, uint value)
    {
        while (value >= 128) { writer.Write((byte)(value | 128)); value >>= 7; }
        writer.Write((byte)value);
    }

    public static byte[] OpenHidden(IReadOnlyList<int> ids)
    {
        if (ids.Count > 29999 || ids.Any(id => id is <= 0 or >= 30000) || ids.Distinct().Count() != ids.Count)
            throw new InvalidDataException("隐藏任务开启列表的编号越界或重复");
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        // Full03 0239EB00：822为count:u32及原序qid:u32，不带成功字节。
        writer.Write(checked((uint)ids.Count));
        foreach (int id in ids) writer.Write(checked((uint)id));
        return bytes.ToArray();
    }

    public static byte[] ActiveList(IReadOnlyList<ActiveMainQuest> active)
    {
        if (active.Count > 30 || active.Select(value => value.Slot).Distinct().Count() != active.Count ||
            active.Any(value => value.Slot > 29 || value.QuestId is 0 or >= 30000))
            throw new InvalidDataException("活动任务列表越界或重复槽");
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(checked((uint)active.Count));
        foreach (var quest in active.OrderBy(value => value.Slot))
        {
            writer.Write(quest.QuestId);
            writer.Write(QuestQuestionProgress.ClientState(quest.ConditionState));
        }
        return bytes.ToArray();
    }

    public static byte[] Accepted(ushort questId, uint scalar, IReadOnlyList<QuestRewardItemProjection> gifts)
    {
        if (questId is 0 or >= 30000 || gifts.Count > byte.MaxValue)
            throw new InvalidDataException("接取任务编号或赠品数组越界");
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write((byte)1);
        writer.Write(questId);
        writer.Write(QuestQuestionProgress.ClientState(scalar));
        writer.Write((byte)gifts.Count);
        // Full03 0236DF22/2E/3A：接取赠品为10B短行，不使用领奖19B行。
        foreach (var gift in gifts)
        {
            if (gift.Item.Uid <= 0 || gift.Item.Position.List != InventoryListType.Main || gift.GrantedCount <= 0 ||
                gift.Item.Core.IsStackable && gift.GrantedCount > gift.Item.Core.Count ||
                !gift.Item.Core.IsStackable && gift.GrantedCount != 1)
                throw new InvalidDataException("接取赠品必须来自实际提交资产");
            writer.Write(checked((ushort)gift.Item.Position.Index));
            writer.Write(checked((uint)gift.Item.Core.ItemId));
            writer.Write(checked((uint)(gift.Item.Core.IsStackable ? gift.GrantedCount : gift.Item.Core.Value)));
        }
        return bytes.ToArray();
    }

    public static byte[] GivenUp(ushort questId)
    {
        if (questId is 0 or >= 30000) throw new InvalidDataException("放弃任务编号越界");
        return [1, (byte)questId, (byte)(questId >> 8)];
    }

    public static byte[] Rejected(ushort error)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)0);
        writer.Write(error);
        return stream.ToArray();
    }

    public static byte[] Finish(ushort questId, byte conditionType, uint experience,
        IReadOnlyList<QuestConsumedItemProjection> consumed, uint gold, IReadOnlyList<QuestRewardItemProjection> rewards, byte? growType = null,
        IReadOnlyList<QuestCurrencyRewardProjection>? currencies = null, ushort tpAmount = 0, bool hellChallenge = false,
        bool creatureEvolution = false, byte? expertJobType = null)
    {
        if (questId is 0 or >= 30000 || !DfoServer.GameWorld.Quests.QuestTargetTypes.IsNative(conditionType) || experience > int.MaxValue || gold > int.MaxValue ||
            consumed.Count > byte.MaxValue || conditionType is not (0 or 25) && consumed.Count != 0 ||
            rewards.Count + (currencies?.Count ?? 0) + (gold == 0 ? 0 : 1) > byte.MaxValue ||
            growType is { } growth && (growth == 0 || (growth & 15) > 5 || growth >> 4 > 2) ||
            growType is not null && (rewards.Count != 0 || currencies?.Count > 0) ||
            tpAmount is not (0 or 2) || tpAmount != 0 && (growType is not null || rewards.Count != 0 || currencies?.Count > 0) ||
            (hellChallenge || creatureEvolution) && (growType is not null || tpAmount != 0 || rewards.Count != 0 || currencies?.Count > 0) ||
            hellChallenge && creatureEvolution ||
            expertJobType is { } expert && (expert is < 1 or > 4 || growType is not null || tpAmount != 0 ||
                hellChallenge || creatureEvolution || rewards.Count != 0 || currencies?.Count > 0))
            throw new InvalidDataException("普通任务交付投影超出已确认合同");
        byte[] rows = FixedRewardRows(rewards,currencies);
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write((byte)1); writer.Write(questId); writer.Write(conditionType); writer.Write(experience);
        if (conditionType is 0 or 25)
        {
            writer.Write((byte)consumed.Count);
            foreach (var row in consumed)
            {
                if (row.Slot < 0 || row.Count <= 0) throw new InvalidDataException("寻物消耗必须来自实际Main持有域");
                // Full03 0237716C→29CA9D0：Main含真实普通槽和已装载的虚拟钱包槽，value为本次扣除数。
                writer.Write((byte)0); writer.Write(checked((ushort)row.Slot)); writer.Write(checked((uint)row.Count));
            }
        }
        if (growType is { } target)
        {
            // Full03 02377DD5读branch/stage，02377E16..02377FF7固定读取两页count；技能完整状态另由19发布。
            writer.Write((byte)(target >> 4 == 0 ? 1 : 2)); writer.Write((byte)(target >> 4 == 0 ? target : target >> 4));
            writer.Write((byte)0); writer.Write((byte)0);
            return bytes.ToArray();
        }
        if (expertJobType is { } job)
        {
            // 02377B66/02377DD5：mode20读取副职业类型及两页技能count，完整双树先由19装载。
            writer.Write((byte)20); writer.Write(job); writer.Write((byte)0); writer.Write((byte)0);
            return bytes.ToArray();
        }
        if (tpAmount != 0)
        {
            // Full03 02377B15..32/48：mode29固定展示2TP，不读取数量或物品数组；真实余额由19发布。
            writer.Write((byte)29);
            return bytes.ToArray();
        }
        if (hellChallenge)
        {
            // 0237807B的mode3经ID2D1打开深渊资格窗，不读物品count，金币另由14校准。
            writer.Write((byte)3);
            return bytes.ToArray();
        }
        if (creatureEvolution)
        {
            // 02377B73的mode10没有追加读字段；进化实体另由106和完整宠物详情同步。
            writer.Write((byte)10);
            return bytes.ToArray();
        }
        writer.Write((byte)0); writer.Write((byte)(rewards.Count + (currencies?.Count ?? 0) + (gold == 0 ? 0 : 1)));
        writer.Write(rows.AsSpan(1));
        if (gold != 0)
        {
            // 金币的Main持有域为slot0/item0；金额为提交增量，不构造带临时UID的实体物品。
            writer.Write((ushort)0); writer.Write(0u); writer.Write(gold);
            writer.Write((byte)0); writer.Write((ushort)0); writer.Write(0u); writer.Write((byte)0); writer.Write((byte)0);
        }
        return bytes.ToArray();
    }

    public static byte[] ClearTrigger(ushort questId, uint scalar)
    {
        if (questId is 0 or >= 30000)
            throw new InvalidDataException("任务编号超出匹配完成表范围");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(questId);
        writer.Write(QuestQuestionProgress.ClientState(scalar));
        return stream.ToArray();
    }

    // 仅为ACK34 mode0的已证奖励数组子段，不含尚未闭合的成功头或mode字节。
    public static byte[] FixedRewardRows(IReadOnlyList<QuestRewardItemProjection> rewards,IReadOnlyList<QuestCurrencyRewardProjection>? currencies = null)
    {
        if (rewards.Count + (currencies?.Count ?? 0) > byte.MaxValue)
            throw new InvalidDataException("任务奖励数组超过u8容量");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)(rewards.Count + (currencies?.Count ?? 0)));
        foreach (var reward in rewards)
        {
            var item = reward.Item;
            item.Core.Validate();
            item.ValidateDetails(allocating: false);
            if (item.Uid <= 0 || item.Position.Index < 0 || (item.Core.Kind == ItemKind.Creature && item.Position.List != InventoryListType.Pet) ||
                (item.Position.List != InventoryListType.Main &&
                    !(item.Position.List == InventoryListType.Pet && item.Core.Kind == ItemKind.Creature && item.Creature is not null)) ||
                item.Core.Kind is not (ItemKind.Equipment or ItemKind.Creature) && !item.Core.IsStackable || reward.GrantedCount <= 0 ||
                !item.Core.IsStackable && reward.GrantedCount != 1 || item.Core.IsStackable && reward.GrantedCount > item.Core.Count)
                throw new InvalidDataException("任务奖励投影必须来自实际提交的普通背包或宠物资产及本次增量");
            writer.Write(checked((ushort)item.Position.Index));
            writer.Write(checked((uint)item.Core.ItemId));
            writer.Write(checked((uint)(item.Core.IsStackable ? reward.GrantedCount : item.Core.Value)));
            ItemListProtocolWriter.WriteAmplification(writer, item.Core);
            writer.Write(unchecked((uint)DfoServer.GameWorld.Items.ItemExpiration.ForClient(item.Core.ExpireTime)));
            writer.Write(item.Core.SortLockFlag);
            writer.Write(item.Core.RemainUseCount);
        }
        foreach (var currency in currencies ?? [])
        {
            if (currency.Slot is not (1 or 2 or >= 354 and <= 365) || currency.ItemId <= 0 || currency.Count <= 0)
                throw new InvalidDataException("钱包奖励需要实际本版持有域和正数增量");
            writer.Write(checked((ushort)currency.Slot)); writer.Write(checked((uint)currency.ItemId)); writer.Write(checked((uint)currency.Count));
            writer.Write(new byte[9]);
        }
        return stream.ToArray();
    }


}

internal sealed record QuestRewardItemProjection(InventoryItem Item, int GrantedCount);
internal sealed record QuestConsumedItemProjection(short Slot, int Count);
internal sealed record QuestCurrencyRewardProjection(short Slot,int ItemId,int Count);
