using DfoServer.Game.Dungeons;
using DfoServer.Game.Titles;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    // 仅冻结攻坚资格，仍需原权威死亡成功后才能消费。
    internal sealed class RaidDeathEligibility
    {
        readonly SinglePlayerDungeonRun run;
        readonly DungeonEntryPlan entry;
        readonly RaidActor actor;
        readonly DungeonRunIdentity identity;
        readonly DungeonMap map;
        readonly ushort monsterKey;
        readonly int monsterId;
        internal RaidKind Kind { get; }
        internal DateTimeOffset CapturedAt { get; }

        private RaidDeathEligibility(SinglePlayerDungeonRun run, RaidActor actor, DungeonPlannedMonster monster,
            RaidKind kind, DateTimeOffset at)
        {
            this.run = run; entry = run.Entry; this.actor = actor; identity = run.Snapshot.Identity;
            map = run.Snapshot.CurrentMap; monsterKey = monster.ObjectKey; monsterId = monster.MonsterId;
            Kind = kind; CapturedAt = at;
        }

        internal static RaidDeathEligibility? Capture(AntonRaidBattle battle, RaidActor actor,
            SinglePlayerDungeonRun dungeon, ushort key)
        {
            lock (battle.gate)
            {
                var identity = dungeon.Snapshot.Identity;
                var at = battle.lukeClock.GetUtcNow();
                var monster = dungeon.CurrentLivingMonster(key);
                if (battle.Kind is not (RaidKind.Anton or RaidKind.Fiendwar or RaidKind.Luke or RaidKind.Prey) ||
                    dungeon.Entry.RaidEntry is not { } raid || raid.Kind != battle.Kind ||
                    raid.InstanceId != battle.Instance || raid.Phase != battle.current.Phase ||
                    battle.current.State != AntonRaidBattleState.Fighting || battle.current.ClockFaulted ||
                    battle.current.PhaseExpired || battle.pendingTransition is not null ||
                    !actor.IsCurrent || !battle.members.ContainsKey(actor) || identity.CharacterId != actor.Character ||
                    dungeon.Snapshot.Phase != SinglePlayerDungeonPhase.Dungeon ||
                    !battle.dungeonRuns.TryGetValue(identity.InstanceId, out var run) || !run.Installed || run.Cleared ||
                    !battle.CurrentObjective(run) || !run.Active.Contains(actor) || run.Phase != raid.Phase ||
                    run.Squad != raid.Squad || run.Dungeon != dungeon.Entry.DungeonId ||
                    !battle.entries.TryGetValue((run.Id, actor.Character), out var binding) ||
                    !ReferenceEquals(binding.Run, run) || binding.Phase != run.Phase || binding.Dungeon != run.Dungeon ||
                    identity.QuestId != binding.Identity.QuestId || identity.TaskAttempt != binding.Identity.TaskAttempt ||
                    identity.Epoch < binding.Identity.Epoch || identity.Epoch < run.MapEpochs.GetValueOrDefault(actor) ||
                    dungeon.Entry.IsPractice || dungeon.Entry.IsTrainingRoom || dungeon.Entry.IsTutorial ||
                    dungeon.Entry.IsClassExperience || dungeon.Entry.IsQuestReplay || dungeon.Entry.IsAltar || dungeon.Entry.IsTagSystem ||
                    monster is null || monster.MonsterId <= 0 || !monster.CanReportDeath || monster.IsFriendly ||
                    monster.IsSpecialChild || monster.AiCharacter is not null)
                    return null;
                if (battle.Kind == RaidKind.Prey &&
                    (battle.lukeCompletion?.Source != PreyGuideRewards.Source || run.Active.Count != 1))
                    return null;
                if (battle.Kind == RaidKind.Fiendwar && (battle.fiendwarDefinition is not { Guide: false } ||
                    !FiendwarTitleFacts.IsTeamDungeon(dungeon.Entry.DungeonId) ||
                    battle.current.Fiendwar is not { Failed: false, Cleared: false } field || at >= field.Deadline ||
                    battle.current.FiendwarExpiredRuns.Contains(run.Id) ||
                    !battle.fiendwarEncounters.TryGetValue(run.Id, out var encounter) || at >= encounter.Deadline))
                    return null;
                return new(dungeon, actor, monster, battle.Kind, at);
            }
        }

        internal bool Matches(SinglePlayerDungeonRun dungeon, PreparedDungeonDeath prepared) =>
            ReferenceEquals(run, dungeon) && ReferenceEquals(entry, dungeon.Entry) && actor.IsCurrent &&
            actor.Character == identity.CharacterId && dungeon.IsPendingDeath(prepared) &&
            identity == dungeon.Snapshot.Identity && identity == prepared.Death.Identity &&
            map == dungeon.Snapshot.CurrentMap && prepared.Death.MonsterKey == monsterKey &&
            prepared.Monster.ObjectKey == monsterKey && prepared.Monster.MonsterId == monsterId;
    }

    internal RaidDeathEligibility? CaptureRaidDeathEligibility(RaidActor actor, SinglePlayerDungeonRun run, ushort key) =>
        RaidDeathEligibility.Capture(this, actor, run, key);
}
