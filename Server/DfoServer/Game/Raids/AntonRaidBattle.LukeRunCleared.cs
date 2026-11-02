using System.Collections.Immutable;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    // 正式Run全员清图后的单次事实；只由原Run和待发布notice引用，不建立历史事件表。
    internal sealed class LukeRunCleared
    {
        internal AntonRaidBattle Battle { get; }
        internal Guid Instance => Battle.Instance;
        internal Guid Run { get; }
        internal int Phase { get; }
        internal uint Dungeon { get; }
        internal ImmutableArray<ushort> Members { get; }
        readonly RaidActor[] recipients;
        bool publishing;
        bool published;
        // 只在Battle锁内读取；表示全部连接已尝试入队，不表示socket已写完。
        internal bool Published => published;

        LukeRunCleared(AntonRaidBattle battle, Guid run, int phase, uint dungeon,
            ImmutableArray<ushort> members, RaidActor[] recipients)
        {
            Battle = battle; Run = run; Phase = phase; Dungeon = dungeon;
            Members = members; this.recipients = recipients;
            // 无协议消费者时没有连接入队义务，领域调用不额外依赖网络发布。
            published = !recipients.Any(actor => actor.Session.RaidRunClearedArrived is not null);
        }

        internal static LukeRunCleared Capture(AntonRaidBattle battle, Guid runId)
        {
            lock (battle.gate)
            {
                if (battle.Kind != RaidKind.Luke || !battle.dungeonRuns.TryGetValue(runId, out var run) ||
                    !run.Installed || !run.Cleared || run.ClearedMembers.IsDefaultOrEmpty ||
                    run.ClearedMembers.Length > 4) throw new InvalidOperationException("不存在正式Run已提交的清图事实");
                return run.LukeClear ??= new(battle, run.Id, run.Phase, run.Dungeon, run.ClearedMembers,
                    battle.members.Keys.ToArray());
            }
        }

        internal void Publish()
        {
            lock (Battle.gate)
            {
                if (publishing || published) return;
                publishing = true;
            }
            // 这里只入原连接有界FIFO，不等socket；队列拒绝会关闭该连接，无需重造事件。
            // 任何接收者异常均继续通知剩余原成员，最后保留原异常。
            List<Exception>? failures = null;
            try
            {
                foreach (var actor in recipients)
                {
                    try { if (actor.IsCurrent) actor.Session.RaidRunClearedArrived?.Invoke(this, actor); }
                    catch (Exception error) { (failures ??= []).Add(error); }
                }
            }
            finally
            {
                lock (Battle.gate) { published = true; publishing = false; }
            }
            if (failures is { Count: 1 }) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures is { Count: > 1 }) throw new AggregateException(failures);
        }
    }

    internal LukeRunCleared? CaptureLukeRunCleared(Guid run)
    {
        lock (gate) return dungeonRuns.GetValueOrDefault(run)?.LukeClear;
    }

    // 原Run事件拥有通关瞬态；怪物/名册等较早快照不能抢先重播相同Clear横幅。
    internal bool HasPendingLukeRunClear(AntonRaidBattleView view, int dungeon)
    {
        lock (gate) return ReferenceEquals(current, view) && dungeonRuns.Values.Any(run =>
            run.Dungeon == dungeon && run.LukeClear is { Published: false });
    }
}
