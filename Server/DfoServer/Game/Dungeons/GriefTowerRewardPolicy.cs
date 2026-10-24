using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed record GriefTowerRewardItem(int Slot, int ItemId, int Count);
internal sealed record GriefTowerReward(string Group, ImmutableArray<GriefTowerRewardItem> Items)
{
    public string Rule => GriefTowerRewardPolicy.Rule;
}

// 公开说明只确认随机材料、50/100层固定材料及后续隐藏罐；没有公开精确抽样器。
// 本服推定：配置槽各抽一次，槽内按万分制累计互斥，剩余为空奖。不得标成原码还原。
internal static class GriefTowerRewardPolicy
{
    public const string Rule = "grief-pvf-independent-slots-inferred-v1";
    public const int Denominator = 10000;

    public static GriefTowerReward Prepare(GriefTowerCatalog catalog, GriefTowerFloor floor, Func<int, int>? draw = null)
    {
        if (!catalog.FloorsByDungeon.TryGetValue(floor.DungeonId, out var declared) || declared != floor ||
            !catalog.RewardTables.TryGetValue(floor.RewardGroup, out var slots))
            throw new InvalidDataException("悲叹奖励楼层或组不属于当前资源");
        return Select(floor.RewardGroup, slots, draw ?? Random.Shared.Next);
    }

    internal static GriefTowerReward Select(string group, ImmutableArray<GriefTowerRewardSlot> slots, Func<int, int> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        if (string.IsNullOrWhiteSpace(group) || slots.IsDefaultOrEmpty || slots.Length > 10)
            throw new InvalidDataException("悲叹奖励组缺失或槽数越界");
        // 先核对整张表，坏配置不能消耗部分随机序列后才被发现。
        int previous = 0;
        foreach (var slot in slots)
        {
            if (slot.Number <= previous || slot.Number > 10 || slot.Items.IsDefaultOrEmpty ||
                slot.Items.Any(item => item.ItemId <= 0 || item.Count <= 0 || item.Weight <= 0) ||
                slot.Items.Sum(item => (long)item.Weight) > Denominator)
                throw new InvalidDataException("悲叹奖励槽顺序、候选或万分制总值非法");
            previous = slot.Number;
        }
        var selected = ImmutableArray.CreateBuilder<GriefTowerRewardItem>(slots.Length);
        foreach (var slot in slots)
        {
            int roll = draw(Denominator);
            if (roll < 0 || roll >= Denominator) throw new InvalidDataException("悲叹奖励随机值越界");
            foreach (var item in slot.Items)
            {
                if (roll >= item.Weight) { roll -= item.Weight; continue; }
                selected.Add(new(slot.Number, item.ItemId, item.Count));
                break;
            }
        }
        return new(group, selected.ToImmutable());
    }
}
