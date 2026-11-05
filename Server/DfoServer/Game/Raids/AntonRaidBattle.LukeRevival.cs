using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 只授权手动消耗本人复活币；原资源与显式装配共同校验，不包含祝福或免耗复活。
internal sealed record LukeRaidRevivalPolicy(string Source)
{
    public const int DungeonLimit = 8;
    public const int PhaseLimit = 20;
}

internal sealed partial class AntonRaidBattle
{
    readonly LukeRaidRevivalPolicy? lukeRevival;
    readonly HashSet<RevivalReservation> lukeRevivalReservations = [];
    internal bool SupportsLukeRevival => Kind == RaidKind.Luke && lukeRevival is not null ||
        Kind == RaidKind.Fiendwar && fiendwarDefinition is not null || Kind == RaidKind.Prey && !PreyGuide;

    // 当前攻坚成员的死亡事实；能力对象私有铸造，SQL前只预留，成功后不再按动态阶段拒绝。
    internal sealed class RevivalReservation : IDisposable
    {
        readonly AntonRaidBattle battle;
        readonly RaidDungeonRun run;
        readonly RaidActor actor;
        readonly long death;
        bool finished;
        internal bool BlocksEntry(RaidDungeonIdentity identity) => identity.PreyPartyId is { } party ? run.PreyPartyId == party :
            identity.PreySoloCharacter is { } character ? run.PreySoloCharacter == character : run.Squad == identity.Squad;
        internal AntonRaidBattleChange? Change { get; private set; }
        private RevivalReservation(AntonRaidBattle battle, RaidDungeonRun run, RaidActor actor, long death)
        { this.battle = battle; this.run = run; this.actor = actor; this.death = death; }

        internal static RevivalReservation? Prepare(AntonRaidBattle battle, RaidActor actor,
            DungeonRunIdentity identity, long death)
        {
            lock (battle.gate)
            {
                if (!battle.SupportsLukeRevival || !actor.IsCurrent || death <= 0 ||
                    battle.pendingTransition is not null || TerminalRequested(battle.current) || battle.LukePhaseDueLocked() ||
                    !battle.members.ContainsKey(actor) || !battle.dungeonRuns.TryGetValue(identity.InstanceId, out var run) ||
                    !run.Installed || !run.Active.Contains(actor) || run.Phase != battle.current.Phase ||
                    battle.Kind == RaidKind.Prey && (battle.current.PreyExpelledRuns.Contains(run.Id) || battle.PreyDungeonTimeDueLocked(run)) ||
                    battle.Kind == RaidKind.Fiendwar && (!battle.fiendwarEncounters.TryGetValue(run.Id, out var encounter) || battle.lukeClock.GetUtcNow() >= encounter.Deadline) ||
                    battle.current.DungeonGenerations.GetValueOrDefault(checked((int)run.Dungeon)) != run.Generation ||
                    battle.current.Dungeons.GetValueOrDefault(checked((int)run.Dungeon)) is not (RaidDungeonState.Open or RaidDungeonState.Clear) ||
                    !battle.entries.TryGetValue((identity.InstanceId, actor.Character), out var entry) ||
                    !ReferenceEquals(entry.Run, run) || identity.CharacterId != actor.Character ||
                    identity.QuestId != entry.Identity.QuestId || identity.TaskAttempt != entry.Identity.TaskAttempt ||
                    identity.Epoch < entry.Identity.Epoch || death <= run.RevivedDeaths.GetValueOrDefault(actor) ||
                    battle.lukeRevivalReservations.Count >= battle.members.Count || battle.lukeRevivalReservations.Any(row => row.actor == actor))
                    return null;
                bool phasePool = battle.Kind == RaidKind.Luke && run.Phase == 1;
                int used = phasePool ? battle.current.LukePhaseCoinsUsed ?? 0 : run.RevivalUsed;
                int reserved = battle.lukeRevivalReservations.Count(row => phasePool ? row.run.Phase == run.Phase : ReferenceEquals(row.run, run));
                // 普雷两阶段、漩涡团队均消费入场时冻结的DGN共享上限；漩涡引导保留原无限额。
                int? limit = battle.Kind != RaidKind.Luke ? run.RevivalLimit : phasePool ? LukeRaidRevivalPolicy.PhaseLimit : LukeRaidRevivalPolicy.DungeonLimit;
                if (limit is { } maximum && used + reserved >= maximum) return null;
                var reservation = new RevivalReservation(battle, run, actor, death);
                battle.lukeRevivalReservations.Add(reservation);
                return reservation;
            }
        }

        internal void Commit()
        {
            lock (battle.gate)
            {
                if (finished) return;
                finished = true;
                var before = battle.current;
                run.RevivalUsed++;
                run.RevivedDeaths[actor] = death;
                if (battle.Kind == RaidKind.Luke && run.Phase == 1)
                    battle.current = before with { LukePhaseCoinsUsed = checked((byte)((before.LukePhaseCoinsUsed ?? 0) + 1)),
                        Revision = before.Revision + 1 };
                else
                    battle.current = before with { Revision = checked(before.Revision + 1) };
                battle.lukeRevivalReservations.Remove(this);
                var transition = battle.CommitPendingTransition();
                if (transition is not null || !ReferenceEquals(before, battle.current)) Change = new(before, battle.current);
            }
        }

        public void Dispose()
        {
            lock (battle.gate)
            {
                if (finished) return;
                finished = true;
                battle.lukeRevivalReservations.Remove(this);
                Change = battle.CommitPendingTransition();
            }
        }
    }

    internal RevivalReservation? ReserveLukeRevival(RaidActor actor, DungeonRunIdentity identity, long death) =>
        RevivalReservation.Prepare(this, actor, identity, death);
}
