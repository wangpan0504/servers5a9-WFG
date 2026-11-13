namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    readonly Dictionary<(int Phase, ushort Character, int Dungeon), (long Anchor, int Seconds)> preyMemberRetries = [];

    internal bool PreyMemberRetryReady(RaidActor actor, int dungeon)
    {
        lock (gate) return PreyMemberRetryReadyLocked(actor.Character, dungeon);
    }

    bool PreyMemberRetryReadyLocked(ushort character, int dungeon) => Kind != RaidKind.Prey || PreyGuide ||
        !preyMemberRetries.TryGetValue((current.Phase, character, dungeon), out var retry) ||
        lukeClock.GetElapsedTime(retry.Anchor) >= TimeSpan.FromSeconds(retry.Seconds);

    // 本服规则：未清图的正式成员离场时起算；期限按角色保留，原团重返不重新计时。
    void RecordPreyMemberRetry(RaidActor actor, RaidDungeonRun run)
    {
        if (Kind != RaidKind.Prey || PreyGuide || !run.Installed || run.Cleared || run.Phase != current.Phase ||
            current.State != AntonRaidBattleState.Fighting || current.PhaseExpired || current.ClockFaulted ||
            pendingTransition is not null || TerminalRequested(current) || LukePhaseDueLocked() ||
            !CurrentObjective(run) && !current.PreyExpelledRuns.Contains(run.Id)) return;
        var rows = script.Phases[run.Phase].Dungeons.Single(row => row.Id == run.Dungeon).Declarations
            .Where(row => row.Tag == "[member retry time]").ToArray();
        if (rows.Length == 0) return;
        if (rows is not [{ Values: [{ Type: 0, Number: >= 0 } seconds] }])
            throw new InvalidDataException("普雷成员重试时间声明无效");
        if (seconds.Number == 0) return;
        int dungeon = checked((int)run.Dungeon);
        var key = (actor.Character, dungeon);
        preyMemberRetries[(run.Phase, actor.Character, dungeon)] = (lukeClock.GetTimestamp(), seconds.Number);
        uint deadline = checked((uint)(lukeClock.GetUtcNow().ToUnixTimeSeconds() + seconds.Number));
        current = current with { PreyMemberRetryDeadlines = current.PreyMemberRetryDeadlines.SetItem(key, deadline) };
    }
}
