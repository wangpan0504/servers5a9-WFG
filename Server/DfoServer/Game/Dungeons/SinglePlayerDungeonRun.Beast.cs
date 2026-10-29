using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed partial class SinglePlayerDungeonRun
{
    internal bool ExpireBeast()
    {
        if (!BeastEncounterPlan.IsNative(entry) || Phase is not (SinglePlayerDungeonPhase.Loading or SinglePlayerDungeonPhase.Dungeon) ||
            HasPendingDeath || HasPendingSettlement || HasPendingPassiveClear || !CanAdvance()) return false;
        Phase = SinglePlayerDungeonPhase.TimedOut;
        Advance();
        return true;
    }

    Room BindBeastRoom(DungeonPoint sentinel)
    {
        int stage = beastBindings.Count;
        if (stage >= 6 || beastCompletedStages != stage || !beastCandidates.TryGetValue((sentinel, stage), out var room))
            throw new InvalidOperationException("魔兽哨兵不能绑定未完成遭遇之后的阶段");
        beastBindings.Add(sentinel, stage);
        rooms[sentinel] = room;
        return room;
    }
}
