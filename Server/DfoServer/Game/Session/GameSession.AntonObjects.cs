using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Session;

internal sealed record AntonNativeObjectContext(AntonDungeonObjectScope Scope,
    DungeonRandomizedObjectSelection Selection, RaidActor Actor, AntonRaidBattle Battle);

internal sealed partial class GameSession
{
    internal FormalDungeonReceipt? CurrentAntonNativeScene(FormalDungeonReceipt anchor) => owner.Execute(() =>
    {
        if (!Ready || formalDungeon is not { Entry.DungeonId: 218 } ||
            anchor.Entry.DungeonId != 218 || owner.Generation != anchor.Generation ||
            SelectionEpoch != anchor.SelectionEpoch || Selected?.Id != anchor.Character.Id ||
            Phase is not (SessionPhase.Dungeon or SessionPhase.Settlement)) return null;
        var current = FormalReceipt();
        // MAI绑定一次实际房间访问，而非某次业务提交；回访相同格也必须拥有新的加载身份。
        return anchor.RoomLoadingIdentity is not null && current.RoomLoadingIdentity == anchor.RoomLoadingIdentity &&
            current.Snapshot.CurrentMap == anchor.Snapshot.CurrentMap && current.MapSeed == anchor.MapSeed &&
            CanPublishNativeScene(current) ? current : null;
    });

    internal AntonNativeObjectContext? CaptureAntonNativeObjectContext(FormalDungeonReceipt scene) => owner.Execute(() =>
    {
        if (!Ready || !CanMutatePartyDungeon || !CanPublishNativeScene(scene) || !CanMoveWithParty() ||
            raidCombat is not { Actor.IsCurrent: true, Battle.Kind: RaidKind.Anton } raid ||
            formalDungeon is not { Entry.DungeonId: 218, Entry.RandomizedObjectSelection: { } selection } dungeon ||
            dungeon.Entry.RaidEntry?.InstanceId != raid.Battle.Instance ||
            raid.Battle.CaptureAntonObjectScope(raid.Actor, dungeon.Run.Snapshot) is not { } scope) return null;
        return new AntonNativeObjectContext(scope, selection, raid.Actor, raid.Battle);
    });
}
