using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class InventoryNpcFavorMaxService
{
    public static InventoryResult Plan(Character character, ImmutableArray<NpcFavorTarget> targets)
    {
        if (targets.IsDefaultOrEmpty || targets.Length > byte.MaxValue ||
            targets.Any(row => row.OwnerId <= 0 || row.Maximum <= 0) ||
            targets.Select(row => row.OwnerId).Distinct().Count() != targets.Length) return InventoryResult.Reject();
        var before = character.Inventory;
        var rows = before.NpcFavor.ToDictionary(row => row.NpcId);
        foreach (var target in targets)
            rows[target.OwnerId] = rows.TryGetValue(target.OwnerId, out var previous)
                ? previous with { Points = target.Maximum } : new(target.OwnerId, target.Maximum);
        if (rows.Count > byte.MaxValue) return InventoryResult.Reject();
        var points = rows.Values.OrderBy(row => row.NpcId).ToImmutableArray();
        var after = before.NpcFavor.SequenceEqual(points) ? before : before with { NpcFavor = points };
        return new(true, 0, new(before, after, "gm_npc_favor_max", new(character)));
    }
}
