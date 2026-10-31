using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal static class OrdinaryDungeonContinuation
{
    // null 表示序列终点；非序列副本仍保留再次挑战当前图的语义。
    public static uint? Target(DungeonEntryPlan entry, DungeonCatalog catalog)
    {
        if (!entry.IsOrdinary || entry.IsPractice || entry.RaidEntry is not null ||
            entry.HellParty is not null || entry.IsCosmo || entry.IsInfernoParty)
            return entry.DungeonId;
        if (OrdinaryLukeProgress.Applies(entry))
            return entry.DungeonId < 5106 ? entry.DungeonId + 1 : null;
        if (PreyNormalProgress.Applies(entry))
        {
            if (entry.PreyNormal is not { } admission) return null;
            var route = admission.Catalog.Route(admission.Branch);
            int index = route.IndexOf(checked((int)entry.DungeonId));
            return index >= 0 && index + 1 < route.Length ? checked((uint)route[index + 1]) : null;
        }
        if (catalog.Get(checked((int)entry.DungeonId)) is { IsOrdinaryAnton: true } definition &&
            definition.Path.StartsWith("dungeon/anton_ordinary/", StringComparison.OrdinalIgnoreCase))
            return catalog.AntonNormalSequence.Next(checked((int)entry.DungeonId));
        return entry.DungeonId;
    }
}
