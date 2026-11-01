using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal bool HasCurrentFiendwarDynamic(ushort key) => owner.Execute(() => formalDungeon is { } dungeon &&
        dungeon.Run.FindCurrentFiendwarDynamic(dungeon.Run.Snapshot.Identity, key, includeRetired: true) is not null);

    // 队伍先为每个原Run独立签发并检查，全部通过之后才逐成员退休；不复用报告人的authority或identity。
    internal bool PrepareFiendwarDynamicDeath(NativeDungeonDeathObservation? normal,
        NativeDungeonScriptedDeathObservation? scripted) => owner.Execute(() =>
    {
        if (!CanMutatePartyDungeon || !CanMoveWithParty() || formalDungeon is not { } dungeon ||
            dungeon.Run.HasPendingPassiveClear || normal is null && scripted is null) return false;
        uint key = normal?.MonsterKey ?? scripted!.MonsterKey;
        if (key > ushort.MaxValue || !HasCurrentFiendwarDynamic((ushort)key)) return false;
        var issued = normal is not null
            ? dungeon.Authority.ValidateAndIssue(dungeon.Run, dungeon.Run.Snapshot.Identity, normal)
            : dungeon.Authority.ValidateAndIssueScripted(dungeon.Run, dungeon.Run.Snapshot.Identity, scripted!);
        return issued.Death is not null &&
            dungeon.Run.CanRetireCurrentFiendwarDynamic(issued.Death) is DungeonRunOutcome.Accepted or DungeonRunOutcome.Duplicate;
    });
}
