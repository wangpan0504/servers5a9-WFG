using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal int EpicDropPotionBonus(Character character, DateTimeOffset at, bool hellRewardOnly = false)
    {
        if (consumables is null) return 0;
        long now = at.ToUnixTimeMilliseconds();
        return character.Inventory.ConsumableStates.Where(row => row.Kind == ConsumableStateKind.Effect && row.Active(now))
            .Select(row => consumables.EpicDropBuffs.GetValueOrDefault(row.ItemId))
            .Where(definition => definition is not null && definition.HellRewardOnly == hellRewardOnly)
            .Select(definition => definition!.Percent)
            .DefaultIfEmpty().Max();
    }

    internal int HellLegendEpicDropPotionBonus(Character character, DateTimeOffset at)
    {
        if (consumables is null) return 0;
        long now = at.ToUnixTimeMilliseconds();
        return character.Inventory.ConsumableStates.Where(row => row.Kind == ConsumableStateKind.Effect && row.Active(now))
            .Select(row => consumables.HellLegendEpicDropBuffs.GetValueOrDefault(row.ItemId)?.Param ?? 0)
            .DefaultIfEmpty().Max();
    }
}
