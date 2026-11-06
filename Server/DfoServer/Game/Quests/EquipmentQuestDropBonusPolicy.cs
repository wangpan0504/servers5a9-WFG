using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Quests;

internal static class EquipmentQuestDropBonusPolicy
{
    internal static bool AttributeSlot(InventorySlot slot) =>
        slot.List == InventoryListType.Equipment && slot.Index is not (11 or 33) || ItemSlotBoundService.IsEquippedRune(slot);

    public static ImmutableArray<int> Capture(Character character, Func<int, ItemTemplate> template, long now)
    {
        var result = ImmutableArray.CreateBuilder<int>();
        var pieces = new Dictionary<int, int>();
        var tiers = new Dictionary<(int Set, int Pieces), int>();
        foreach (var item in character.Inventory.Items)
        {
            // 只读真实属性槽，不把包裹、换装登记或克隆借用的原件再次计入。
            if (!AttributeSlot(item.Position)) continue;
            var definition = template(item.Core.ItemId);
            if (definition.Kind != item.Core.Kind || item.IsExpired(now) && !definition.UsableExpired || definition.CreatureEgg)
                continue;
            if (definition.QuestItemDropRateBonus != 0) result.Add(definition.QuestItemDropRateBonus);
            foreach (int set in definition.PartSetIds.Where(id => id > 0).Distinct())
                pieces[set] = pieces.GetValueOrDefault(set) + 1;
            foreach (var tier in definition.QuestItemDropSetBonuses)
                tiers[(tier.SetId, tier.RequiredPieces)] = tier.Percent;
        }
        foreach (var (key, percent) in tiers)
            if (pieces.GetValueOrDefault(key.Set) >= key.Pieces && percent != 0) result.Add(percent);
        return result.ToImmutable();
    }

    // df_game_r对每件装备先做整数除法再求和；低概率下不能先合并百分比。
    public static long AdditionalRate(int basis, ImmutableArray<int> percentages)
    {
        if (basis is < 0 or > 100) throw new InvalidDataException("任务掉落原概率越界");
        long total = 0;
        foreach (int percent in percentages) total = checked(total + (long)basis * percent / 100);
        return total;
    }
}
