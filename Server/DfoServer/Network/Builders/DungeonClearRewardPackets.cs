using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders;

// 名称绑定02393C80当前消费者的实际owner，不把参考版本的业务名称当公式。
internal readonly record struct DungeonClearRewardBase(uint ResultUi644, uint ResultUi5E4Index1,
    uint ResultUi5E4Index2, uint ResultUi5E4Index3, byte ResultUi63C);

internal readonly record struct DungeonClearRewardBonus(uint ResultUi5E4Index6, uint ResultUi634,
    uint ResultUi5E4Index5, uint ResultUi5E4Index9, uint ResultUi638, uint ResultUi5E4Index4,
    uint ResultUi5E4Index8, uint ResultUi5E4Index10, uint ResultUi5E4Index11, uint ResultUi5E4Index12,
    uint ResultUi5E4Index13, uint ResultUi46CIndex5, uint ResultUi43CIndex0, uint ResultUi43CIndex8,
    uint ResultUi43CIndex5SecondPart, uint ResultUi43CIndex2, uint ResultUi43CIndex3, uint ResultUi43CIndex6,
    uint ResultUi43CIndex7, uint ResultUi43CIndex4, uint ResultUi43CIndex1, uint Reserved02394130);

internal readonly record struct DungeonClearRewardPost(uint ResultUi5E4Index14, uint ResultUi5E4Index15,
    uint ResultUi5E4Index16, uint ResultUi5E4Index17, uint ResultUi5E4Index18, uint ResultUi43CIndex11,
    uint ResultUi5E4Index19);
internal readonly record struct DungeonClearRewardStatistics(uint StatsB48Index0, uint StatsB48Index1,
    uint StatsB48Index2, uint StatsB48Index3);
internal readonly record struct DungeonClearRewardPair(uint Key, uint Value);
internal readonly record struct DungeonClearRewardMemberCandidates(byte MemberIndex,
    ImmutableArray<DungeonCardDisplayItem> Items);

// 15B没有UID字段，但输入必须是提交后的真实物品；堆叠WireValue为此次新奖励增量，不能用最终堆数量。
// 装备WireValue为Core.Value品质。已经用NOTI14/拾取发布的增量不能再放进本通知。
internal sealed record DungeonClearRewardDirectItem(InventoryItem CommittedItem, uint WireValue);

// 02394C2A只读取弃用；02394C3E控制卡规则分支，不是ShowResult。
// 39DF730实际写70C=第一个输入、710=第二个输入减第一个，必须保留两个原输入。
internal readonly record struct DungeonClearRewardTail(uint Reserved02394C2A, byte CardRuleFlag02394C3E,
    bool ResultUi684, uint ResultUi708, uint ResultUi70C, uint ResultUi70C710Endpoint, uint ResultUi46CIndex6);

internal sealed record DungeonClearRewardValues(DungeonClearRewardBase Base, DungeonClearRewardBonus Bonus,
    ImmutableArray<DungeonClearRewardPair> ResultUi4DC, ImmutableArray<DungeonClearRewardPair> ResultUi4F0,
    DungeonClearRewardPost Post, DungeonClearRewardStatistics Statistics,
    ImmutableArray<DungeonClearRewardPair> StatsB34, ImmutableArray<DungeonClearRewardDirectItem> DirectItems,
    ImmutableArray<DungeonClearRewardMemberCandidates> FreeCandidates, uint PaidCardGoldCost,
    ImmutableArray<DungeonClearRewardMemberCandidates> Mode1Candidates,
    ImmutableArray<DungeonClearRewardMemberCandidates> Mode2Candidates, uint Global062EFA4C,
    DungeonClearRewardTail Tail, ImmutableArray<bool> MemberAppearanceFlags79D);

internal static class DungeonClearRewardPackets
{
    public const int MinimumBodySize = 218;
    // GameFrame的普通正文上限；不能先分配超帧列表再截断。
    public const int MaximumBodySize = 65520;

    public static byte[] Build(DungeonClearRewardValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ValidateU8Pairs(values.ResultUi4DC);
        ValidateU8Pairs(values.ResultUi4F0);
        if (values.StatsB34.IsDefault || values.DirectItems.IsDefault || values.DirectItems.Length > byte.MaxValue ||
            values.MemberAppearanceFlags79D.IsDefault || values.MemberAppearanceFlags79D.Length != 8)
            throw new InvalidDataException("结算列表必须显式提供；直接物品最多255行，尾部必须完整8成员标志");
        long pairs = values.ResultUi4DC.Length + (long)values.ResultUi4F0.Length + values.StatsB34.Length;
        pairs += ValidateCandidates(values.FreeCandidates);
        pairs += ValidateCandidates(values.Mode1Candidates);
        pairs += ValidateCandidates(values.Mode2Candidates);
        long length = MinimumBodySize + values.DirectItems.Length * 15L + pairs * 8L;
        if (length > MaximumBodySize)
            throw new InvalidDataException("完整结算正文超过普通帧65520B上限");
        var slots = new HashSet<short>();
        foreach (var reward in values.DirectItems)
        {
            ArgumentNullException.ThrowIfNull(reward);
            var item = reward.CommittedItem;
            ArgumentNullException.ThrowIfNull(item);
            item.Core.Validate();
            item.ValidateDetails(false);
            var representedCore = new ItemCore(item.Core.ItemId, item.Core.Kind, item.Core.Value)
            {
                Durability = item.Core.Durability, Attr = item.Core.Attr, SealFlag = item.Core.SealFlag
            };
            if (item.Uid <= 0 || item.Position.List != InventoryListType.Main || item.Position.Index < 0 ||
                !slots.Add(item.Position.Index) || reward.WireValue > int.MaxValue ||
                item.Avatar is not null || item.Creature is not null || item.Core != representedCore ||
                (item.Core.IsStackable ? reward.WireValue == 0 || reward.WireValue > item.Core.Value :
                    reward.WireValue != item.Core.Value))
                throw new InvalidDataException("直接奖励须为完整15B可表达的提交后唯一Main槽及实际增量/品质；扩展核心另走完整库存通知");
        }
        using var stream = new MemoryStream((int)length);
        using var writer = new BinaryWriter(stream);
        WriteBase(writer, values.Base);
        WriteBonus(writer, values.Bonus);
        WriteU8Pairs(writer, values.ResultUi4DC);
        WriteU8Pairs(writer, values.ResultUi4F0);
        WritePost(writer, values.Post);
        writer.Write(values.Statistics.StatsB48Index0);
        writer.Write(values.Statistics.StatsB48Index1);
        writer.Write(values.Statistics.StatsB48Index2);
        writer.Write(values.Statistics.StatsB48Index3);
        writer.Write(values.StatsB34.Length); // 02394585读取i32，后续按正数循环。
        WritePairs(writer, values.StatsB34);
        writer.Write((byte)values.DirectItems.Length);
        foreach (var reward in values.DirectItems)
        {
            var item = reward.CommittedItem;
            writer.Write((ushort)item.Position.Index);
            writer.Write((uint)item.Core.ItemId);
            writer.Write(reward.WireValue);
            writer.Write(item.Core.Durability);
            writer.Write(item.Core.Attr);
            writer.Write((ushort)item.Core.SealFlag);
        }
        WriteCandidates(writer, values.FreeCandidates);
        writer.Write(values.PaidCardGoldCost);
        WriteCandidates(writer, values.Mode1Candidates);
        WriteCandidates(writer, values.Mode2Candidates);
        writer.Write(values.Global062EFA4C);
        writer.Write(values.Tail.Reserved02394C2A);
        writer.Write(values.Tail.CardRuleFlag02394C3E);
        writer.Write((byte)(values.Tail.ResultUi684 ? 1 : 0));
        writer.Write(values.Tail.ResultUi708);
        writer.Write(values.Tail.ResultUi70C);
        writer.Write(values.Tail.ResultUi70C710Endpoint);
        writer.Write(values.Tail.ResultUi46CIndex6); // 覆盖此前Bonus的46C[6]，不是第二份独立加成。
        foreach (bool flag in values.MemberAppearanceFlags79D)
            writer.Write((byte)(flag ? 1 : 0));
        if (stream.Length != length)
            throw new InvalidDataException("结算编码宽度与完整消费者合同不一致");
        return stream.ToArray();
    }

    private static long ValidateCandidates(ImmutableArray<DungeonClearRewardMemberCandidates> members)
    {
        if (members.IsDefault || members.Length != 8)
            throw new InvalidDataException("候选卡必须显式提供完整8成员列表");
        long count = 0;
        for (int index = 0; index < members.Length; index++)
        {
            var member = members[index];
            if (member.MemberIndex != index || member.Items.IsDefault || member.Items.Length > byte.MaxValue)
                throw new InvalidDataException("候选成员必须按owner0..7顺序完整提供，每成员最多255个条目");
            count += member.Items.Length;
        }
        return count;
    }

    private static void ValidateU8Pairs(ImmutableArray<DungeonClearRewardPair> pairs)
    {
        if (pairs.IsDefault || pairs.Length > byte.MaxValue)
            throw new InvalidDataException("结算分量列表必须显式提供且最多255项");
    }

    private static void WriteBase(BinaryWriter writer, DungeonClearRewardBase value)
    {
        writer.Write(value.ResultUi644);
        writer.Write(value.ResultUi5E4Index1);
        writer.Write(value.ResultUi5E4Index2);
        writer.Write(value.ResultUi5E4Index3);
        writer.Write(value.ResultUi63C);
    }

    private static void WriteBonus(BinaryWriter writer, DungeonClearRewardBonus value)
    {
        writer.Write(value.ResultUi5E4Index6);
        writer.Write(value.ResultUi634);
        writer.Write(value.ResultUi5E4Index5);
        writer.Write(value.ResultUi5E4Index9);
        writer.Write(value.ResultUi638);
        writer.Write(value.ResultUi5E4Index4);
        writer.Write(value.ResultUi5E4Index8);
        writer.Write(value.ResultUi5E4Index10);
        writer.Write(value.ResultUi5E4Index11);
        writer.Write(value.ResultUi5E4Index12);
        writer.Write(value.ResultUi5E4Index13);
        writer.Write(value.ResultUi46CIndex5);
        writer.Write(value.ResultUi43CIndex0);
        writer.Write(value.ResultUi43CIndex8);
        writer.Write(value.ResultUi43CIndex5SecondPart);
        writer.Write(value.ResultUi43CIndex2);
        writer.Write(value.ResultUi43CIndex3);
        writer.Write(value.ResultUi43CIndex6);
        writer.Write(value.ResultUi43CIndex7);
        writer.Write(value.ResultUi43CIndex4);
        writer.Write(value.ResultUi43CIndex1);
        writer.Write(value.Reserved02394130);
    }

    private static void WritePost(BinaryWriter writer, DungeonClearRewardPost value)
    {
        writer.Write(value.ResultUi5E4Index14);
        writer.Write(value.ResultUi5E4Index15);
        writer.Write(value.ResultUi5E4Index16);
        writer.Write(value.ResultUi5E4Index17);
        writer.Write(value.ResultUi5E4Index18);
        writer.Write(value.ResultUi43CIndex11);
        writer.Write(value.ResultUi5E4Index19);
    }

    private static void WritePairs(BinaryWriter writer, ImmutableArray<DungeonClearRewardPair> pairs)
    {
        foreach (var pair in pairs)
        {
            writer.Write(pair.Key);
            writer.Write(pair.Value);
        }
    }

    private static void WriteU8Pairs(BinaryWriter writer, ImmutableArray<DungeonClearRewardPair> pairs)
    {
        writer.Write((byte)pairs.Length);
        WritePairs(writer, pairs);
    }

    private static void WriteCandidates(BinaryWriter writer, ImmutableArray<DungeonClearRewardMemberCandidates> members)
    {
        foreach (var member in members)
        {
            writer.Write((byte)member.Items.Length);
            foreach (var item in member.Items)
            {
                writer.Write(item.ItemId);
                writer.Write(item.Value);
            }
        }
    }
}
