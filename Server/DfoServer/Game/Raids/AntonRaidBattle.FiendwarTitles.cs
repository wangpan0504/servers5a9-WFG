using DfoServer.Game.Dungeons;
using DfoServer.Game.Titles;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    // 凭证只由正式Battle铸造；存储层仍复验原Run及原死亡候选，不接受客户端成就计数。
    internal sealed class FiendwarTitleDeath
    {
        internal SinglePlayerDungeonRun Run { get; }
        internal PreparedDungeonDeath Death { get; }
        internal bool NoCoinClear { get; }
        private FiendwarTitleDeath(SinglePlayerDungeonRun run, PreparedDungeonDeath death, bool noCoin)
        { Run = run; Death = death; NoCoinClear = noCoin; }

        internal static FiendwarTitleDeath? Capture(AntonRaidBattle battle, RaidActor actor,
            SinglePlayerDungeonRun dungeon, PreparedDungeonDeath death)
        {
            lock (battle.gate)
            {
                var identity = death.Death.Identity;
                if (battle.Kind != RaidKind.Fiendwar || battle.fiendwarDefinition is not { Guide: false } ||
                    !FiendwarTitleFacts.IsTeamDungeon(dungeon.Entry.DungeonId) || !actor.IsCurrent ||
                    !battle.members.ContainsKey(actor) || !dungeon.IsPendingDeath(death) || battle.current.ClockFaulted ||
                    battle.current.PhaseExpired || battle.pendingTransition is not null ||
                    battle.current.Fiendwar is not { Failed: false, Cleared: false } field ||
                    battle.lukeClock.GetUtcNow() >= field.Deadline ||
                    identity.CharacterId != actor.Character ||
                    !battle.dungeonRuns.TryGetValue(identity.InstanceId, out var run) || !run.Installed ||
                    run.Cleared || !battle.CurrentObjective(run) || battle.current.FiendwarExpiredRuns.Contains(run.Id) ||
                    !battle.fiendwarEncounters.TryGetValue(run.Id, out var encounter) ||
                    battle.lukeClock.GetUtcNow() >= encounter.Deadline ||
                    !run.Active.Contains(actor) || run.Dungeon != dungeon.Entry.DungeonId ||
                    !battle.entries.TryGetValue((run.Id, actor.Character), out var binding) ||
                    !ReferenceEquals(binding.Run, run) || identity.QuestId != binding.Identity.QuestId ||
                    identity.TaskAttempt != binding.Identity.TaskAttempt || identity.Epoch < binding.Identity.Epoch ||
                    identity.Epoch < run.MapEpochs.GetValueOrDefault(actor))
                    return null;
                return new(dungeon, death, death.Result.ClearFact is not null && run.RevivalUsed == 0);
            }
        }
    }

    internal FiendwarTitleDeath? CaptureFiendwarTitleDeath(RaidActor actor, SinglePlayerDungeonRun run,
        PreparedDungeonDeath death) => FiendwarTitleDeath.Capture(this, actor, run, death);
}
