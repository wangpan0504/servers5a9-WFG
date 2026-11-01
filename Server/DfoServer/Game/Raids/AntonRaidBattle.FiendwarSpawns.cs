using DfoServer.Game.Dungeons;
using DfoServer.Game.Titles;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    internal bool CanRegisterFiendwarSpawn(RaidActor actor, SinglePlayerDungeonRun dungeon)
    {
        lock (gate)
        {
            var snapshot = dungeon.Snapshot;
            var identity = snapshot.Identity;
            return Kind == RaidKind.Fiendwar && fiendwarDefinition is { } definition &&
                (definition.Guide ? dungeon.Entry.DungeonId is >= 5350 and <= 5358 : FiendwarTitleFacts.IsTeamDungeon(dungeon.Entry.DungeonId)) &&
                snapshot.Phase == SinglePlayerDungeonPhase.Dungeon && actor.IsCurrent && members.ContainsKey(actor) &&
                !current.ClockFaulted && !current.PhaseExpired && pendingTransition is null &&
                current.Fiendwar is { Failed: false, Cleared: false } battlefield && lukeClock.GetUtcNow() < battlefield.Deadline &&
                dungeonRuns.TryGetValue(identity.InstanceId, out var run) && run.Installed && !run.Cleared &&
                CurrentObjective(run) && !current.FiendwarExpiredRuns.Contains(run.Id) &&
                fiendwarEncounters.TryGetValue(run.Id, out var encounter) && lukeClock.GetUtcNow() < encounter.Deadline &&
                run.Active.Contains(actor) && run.Dungeon == dungeon.Entry.DungeonId && identity.CharacterId == actor.Character &&
                entries.TryGetValue((run.Id, actor.Character), out var binding) && ReferenceEquals(binding.Run, run) &&
                identity.QuestId == binding.Identity.QuestId && identity.TaskAttempt == binding.Identity.TaskAttempt &&
                identity.Epoch >= binding.Identity.Epoch && identity.Epoch >= run.MapEpochs.GetValueOrDefault(actor);
        }
    }
}
