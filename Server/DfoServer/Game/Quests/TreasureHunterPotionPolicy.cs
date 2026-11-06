using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Quests;

internal static class TreasureHunterPotionPolicy
{
    public static int ActiveBonus(IEnumerable<ConsumableState> states,
        IReadOnlyDictionary<int, TreasureHunterPotionDefinition>? definitions, long now)
    {
        if (definitions is null || definitions.Count == 0) return 0;
        int bonus = 0;
        foreach (var state in states)
            if (state.Kind == ConsumableStateKind.Effect && state.Active(now) &&
                definitions.TryGetValue(state.ItemId, out var definition))
                bonus = Math.Max(bonus, definition.BonusRate);
        return bonus;
    }

    // df_game_r按百分制整数抽样；药剂增量以原任务概率计算，不再次放大已有契约加成。
    public static int AdjustedRate(int basis, int current, int bonus)
    {
        if (basis is < 0 or > 100 || current is < 0 or > 100 || bonus < 0)
            throw new InvalidDataException("任务掉落概率或财宝药剂参数无效");
        return (int)Math.Min(100, current + (long)basis * bonus / 10000);
    }
}
