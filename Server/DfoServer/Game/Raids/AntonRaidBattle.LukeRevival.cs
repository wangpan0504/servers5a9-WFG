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
    internal bool SupportsLukeRevival => Kind == RaidKind.Luke && lukeRevival is not null;

    // 最多八个当前角色死亡事实；能力对象私有铸造，SQL前只预留，成功后不再按动态阶段拒绝。
    internal sealed class RevivalReservation : IDisposable
    {
        readonly AntonRaidBattle battle;
        readonly LukeDungeonRun run;
        readonly RaidActor actor;
        readonly long death;
        bool finished;
        internal byte Squad => run.Squad;
        internal AntonRaidBattleChange? Change { get; private set; }
        private RevivalReservation(AntonRaidBattle battle, LukeDungeonRun run, RaidActor actor, long death)
        { this.battle = battle; this.run = run; this.actor = actor; this.death = death; }

        internal static RevivalReservation? Prepare(AntonRaidBattle battle, RaidActor actor,
            DungeonRunIdentity identity, long death)
        {
            lock (battle.gate)
            {
                if (!battle.SupportsLukeRevival || !actor.IsCurrent || death <= 0 ||
                    battle.pendingTransition is not null || TerminalRequested(battle.current) || battle.LukePhaseDueLocked() ||
                    !battle.members.ContainsKey(actor) || !battle.lukeRuns.TryGetValue(identity.InstanceId, out var run) ||
                    !run.Installed || !run.Active.Contains(actor) || run.Phase != battle.current.Phase ||
                    battle.current.DungeonGenerations.GetValueOrDefault(checked((int)run.Dungeon)) != run.Generation ||
                    battle.current.Dungeons.GetValueOrDefault(checked((int)run.Dungeon)) is not (RaidDungeonState.Open or RaidDungeonState.Clear) ||
                    !battle.entries.TryGetValue((identity.InstanceId, actor.Character), out var entry) ||
                    !ReferenceEquals(entry.Luke, run) || identity.CharacterId != actor.Character ||
                    identity.QuestId != entry.Identity.QuestId || identity.TaskAttempt != entry.Identity.TaskAttempt ||
                    identity.Epoch < entry.Identity.Epoch || death <= run.RevivedDeaths.GetValueOrDefault(actor) ||
                    battle.lukeRevivalReservations.Count >= 8 || battle.lukeRevivalReservations.Any(row => row.actor == actor))
                    return null;
                int used = run.Phase == 0 ? run.RevivalUsed : battle.current.LukePhaseCoinsUsed ?? 0;
                int reserved = battle.lukeRevivalReservations.Count(row => run.Phase == 0 ? ReferenceEquals(row.run, run) : row.run.Phase == run.Phase);
                int limit = run.Phase == 0 ? LukeRaidRevivalPolicy.DungeonLimit : LukeRaidRevivalPolicy.PhaseLimit;
                if (used + reserved >= limit) return null;
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
                if (run.Phase == 1)
                    battle.current = before with { LukePhaseCoinsUsed = checked((byte)((before.LukePhaseCoinsUsed ?? 0) + 1)),
                        Revision = before.Revision + 1 };
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
