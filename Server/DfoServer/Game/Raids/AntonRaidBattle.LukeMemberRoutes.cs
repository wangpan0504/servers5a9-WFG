namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    internal bool AllowsLukeMemberRoute(RaidActor actor, int phase, int dungeon)
    {
        lock (gate) return actor.IsCurrent && members.ContainsKey(actor) && phase == current.Phase &&
            AllowsLukeThroneLocked(dungeon) &&
            script.Phases[phase].Dungeons.Any(row => row.Id == dungeon);
    }
}
