using DfoServer.Game.Dungeons;
using DfoServer.Game.Titles;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    internal sealed class FiendwarCounterFact
    {
        readonly AntonRaidBattle battle;
        readonly RaidActor actor;
        readonly SinglePlayerDungeonRun dungeon;
        readonly DungeonRunIdentity identity;
        readonly FiendwarCounterReport report;
        private FiendwarCounterFact(AntonRaidBattle battle, RaidActor actor, SinglePlayerDungeonRun dungeon,
            FiendwarCounterReport report)
        { this.battle = battle; this.actor = actor; this.dungeon = dungeon; identity = dungeon.Snapshot.Identity; this.report = report; }
        internal ushort Character => actor.Character;
        internal bool IsCurrent
        {
            get
            {
                lock (battle.gate)
                {
                    var snapshot = dungeon.Snapshot;
                    if (snapshot.Identity != identity || snapshot.Phase != SinglePlayerDungeonPhase.Dungeon ||
                        battle.Kind != RaidKind.Fiendwar || battle.fiendwarDefinition is not { Guide: false } ||
                        !FiendwarTitleFacts.IsTeamDungeon(dungeon.Entry.DungeonId) || !report.Valid ||
                        report.Dungeon != dungeon.Entry.DungeonId || report.Map != snapshot.CurrentMap.Id ||
                        !actor.IsCurrent || !battle.members.ContainsKey(actor) || battle.current.ClockFaulted ||
                        battle.current.PhaseExpired || battle.pendingTransition is not null ||
                        battle.current.Fiendwar is not { Failed: false, Cleared: false } battlefield ||
                        battle.lukeClock.GetUtcNow() >= battlefield.Deadline ||
                        !battle.dungeonRuns.TryGetValue(identity.InstanceId, out var run) || !run.Installed || run.Cleared ||
                        !battle.CurrentObjective(run) || battle.current.FiendwarExpiredRuns.Contains(run.Id) ||
                        !battle.fiendwarEncounters.TryGetValue(run.Id, out var encounter) ||
                        battle.lukeClock.GetUtcNow() >= encounter.Deadline || !run.Active.Contains(actor) ||
                        run.Dungeon != dungeon.Entry.DungeonId || identity.CharacterId != actor.Character ||
                        !battle.entries.TryGetValue((run.Id, actor.Character), out var binding) || !ReferenceEquals(binding.Run, run) ||
                        identity.QuestId != binding.Identity.QuestId || identity.TaskAttempt != binding.Identity.TaskAttempt ||
                        identity.Epoch < binding.Identity.Epoch || identity.Epoch < run.MapEpochs.GetValueOrDefault(actor)) return false;
                    // 动态光幻影未作为服务端静态演员注册，只允许其三种真实资源，并要求光本体仍在本房存活。
                    return dungeon.CurrentMonsterPlan.Monsters.Any(monster => monster.AiCharacter is null &&
                        snapshot.LivingMonsters.Contains(monster.ObjectKey) &&
                        (report.SourceKind == 1 && monster.MonsterId == report.SourceId && monster.ObjectKey == report.SourceKey ||
                         report.SourceKind == 2 && monster.MonsterId == 107013178));
                }
            }
        }

        internal static FiendwarCounterFact? Capture(AntonRaidBattle battle, RaidActor actor,
            SinglePlayerDungeonRun dungeon, FiendwarCounterReport report)
        {
            var fact = new FiendwarCounterFact(battle, actor, dungeon, report);
            return fact.IsCurrent ? fact : null;
        }
    }
}
