using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Session;

internal sealed record AntonConstructorContext(AntonDungeonObjectScope Scope, AntonConstructorDefinition Definition,
    RaidActor Actor, AntonRaidBattle Battle, SinglePlayerDungeonRun Run);

internal sealed partial class GameSession
{
    internal AntonConstructorContext? CaptureAntonConstructorContext(FormalDungeonReceipt scene) => owner.Execute(() =>
    {
        if (!Ready || !CanMutatePartyDungeon || !CanPublishNativeScene(scene) || !CanMoveWithParty() ||
            raidCombat is not { Actor.IsCurrent: true, Battle.Kind: RaidKind.Anton } raid || formalDungeon is not { } dungeon ||
            dungeon.Entry.RaidEntry?.InstanceId != raid.Battle.Instance ||
            AntonConstructorDefinition.Find(dungeon.Entry.DungeonId, scene.Snapshot.CurrentMap.Id) is not { } definition ||
            raid.Battle.CaptureAntonConstructorScope(raid.Actor, dungeon.Run) is not { } scope) return null;
        return new AntonConstructorContext(scope, definition, raid.Actor, raid.Battle, dungeon.Run);
    });

    internal AntonConstructorDeath? RecordAntonConstructorDeath(FormalDungeonDeathResult result) => owner.Execute(() =>
        result is { Outcome: DungeonRunOutcome.Accepted or DungeonRunOutcome.Duplicate, Receipt: { } scene, ReportedTarget: { } target } &&
        CaptureAntonConstructorContext(scene) is { } context && target.MonsterId == context.Definition.Monster && formalDungeon is { } dungeon
            ? context.Battle.RecordAntonConstructorDeath(context.Actor, dungeon.Run, result.MonsterKey) : null);
}
