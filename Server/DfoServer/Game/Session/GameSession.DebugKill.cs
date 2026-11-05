#if DEBUG
using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal sealed class DebugDungeonKillJob(FormalDungeonReceipt scene, SinglePlayerDungeonRun run,
        DungeonPlannedMonster[] targets)
    {
        internal readonly FormalDungeonReceipt Scene = scene;
        internal readonly SinglePlayerDungeonRun Run = run;
        internal readonly DungeonPlannedMonster[] Targets = targets;
        internal int Cursor;
        internal bool Waiting;
    }

    DebugDungeonKillJob? debugDungeonKill;

    internal DebugDungeonKillJob? CaptureDebugDungeonKill() => owner.Execute(() =>
    {
        if (debugDungeonKill is { } pending && IsDebugDungeonKillCurrent(pending)) return pending;
        debugDungeonKill = null;
        if (!Ready || Selected is null || Phase != SessionPhase.Dungeon || formalDungeon is not { } dungeon ||
            dungeon.CharacterDead || dungeon.Run.Snapshot.Phase != SinglePlayerDungeonPhase.Dungeon ||
            dungeon.Ground.HasPendingOperation || HasPendingQuestScene)
            return null;
        var receipt = FormalReceipt();
        return debugDungeonKill = new(receipt, dungeon.Run, receipt.Monsters.Monsters
            .Where(monster => !monster.IsFriendly && (monster.CanReportDeath || monster.IsSpecialChild) &&
                dungeon.Run.CurrentLivingMonster(monster.ObjectKey) is not null)
            .OrderBy(monster => monster.IsBoss).ThenBy(monster => monster.HellWave?.Order ?? 0).ToArray());
    });

    // 击杀提交会替换Selected；这里只冻结原代次、正式Run和房间，不能沿用角色对象引用。
    bool IsDebugDungeonKillCurrent(DebugDungeonKillJob job)
    {
        var expected = job.Scene;
        return Ready && owner.Generation == expected.Generation && SelectionEpoch == expected.SelectionEpoch &&
            Selected?.Id == expected.Character.Id && Phase is SessionPhase.Dungeon or SessionPhase.Settlement &&
            formalDungeon is { } dungeon && !dungeon.CharacterDead && ReferenceEquals(dungeon.Run, job.Run) &&
            dungeon.Run.Snapshot.CurrentMap == expected.Snapshot.CurrentMap && IsCurrentRaidDungeon(dungeon.Entry) &&
            (dungeon.Run.Snapshot.Identity == expected.Snapshot.Identity || job.Waiting &&
                dungeon.Run.Snapshot.Phase == SinglePlayerDungeonPhase.Settlement &&
                dungeon.Run.ClearFact?.Identity == dungeon.Run.Snapshot.Identity &&
                expected.Snapshot.Identity.Epoch < long.MaxValue && dungeon.Run.Snapshot.Identity ==
                    (expected.Snapshot.Identity with { Epoch = expected.Snapshot.Identity.Epoch + 1 }));
    }

    internal DungeonPlannedMonster? BeginDebugDungeonKillTarget(DebugDungeonKillJob job) => owner.Execute(() =>
    {
        if (!ReferenceEquals(debugDungeonKill, job) || !IsDebugDungeonKillCurrent(job))
        { debugDungeonKill = null; return null; }
        // 失败时先重试原key；共享提交可能已退休本人目标，但其它队员尚未完成提交。
        while (!job.Waiting && job.Cursor < job.Targets.Length &&
            job.Run.CurrentLivingMonster(job.Targets[job.Cursor].ObjectKey) is null) job.Cursor++;
        if (job.Cursor == job.Targets.Length) { debugDungeonKill = null; return null; }
        job.Waiting = true;
        return job.Targets[job.Cursor];
    });

    internal bool CompleteDebugDungeonKillTarget(DebugDungeonKillJob job) => owner.Execute(() =>
    {
        if (!ReferenceEquals(debugDungeonKill, job) || !IsDebugDungeonKillCurrent(job) ||
            job.Run.CurrentLivingMonster(job.Targets[job.Cursor].ObjectKey) is not null) return false;
        job.Cursor++;
        job.Waiting = false;
        if (job.Cursor == job.Targets.Length) debugDungeonKill = null;
        return true;
    });
}
#endif
