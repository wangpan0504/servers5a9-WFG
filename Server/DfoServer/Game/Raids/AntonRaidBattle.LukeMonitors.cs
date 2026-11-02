using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    internal int? LukeMonitorCount(RaidActor actor, DungeonRunIdentity identity)
    {
        lock (gate)
        {
            if (!actor.IsCurrent || !dungeonRuns.TryGetValue(identity.InstanceId, out var run) || !run.Installed ||
                !run.Active.Contains(actor) || run.Phase != current.Phase) return null;
            var targets = LukeMonitorTargets(run);
            return targets.IsEmpty ? null : targets.Length;
        }
    }

    ImmutableArray<LukeMonsterMonitor> LukeMonitorTargets(RaidDungeonRun run) => Kind == RaidKind.Luke
        ? lukeCards?.Monitors?.Find(run.Dungeon, run.MazeOrdinal) ?? [] : [];

    ImmutableArray<uint> RunHpValues(RaidDungeonRun run)
    {
        if (run.Hp is not { } hp) return [];
        // 未知目录不猜形状；已知目录的报告长度已在接纳处严格核对。
        if (Kind != RaidKind.Luke) return hp.Values;
        if (run.Cleared) return hp.Values.Select(_ => 0u).ToImmutableArray();
        var targets = LukeMonitorTargets(run);
        return targets.Length == hp.Values.Length
            ? hp.Values.Select((value, index) => !targets[index].Passive && run.KilledMonitorTargets.Contains(targets[index].Template) ? 0u : value).ToImmutableArray()
            : hp.Values;
    }
}
