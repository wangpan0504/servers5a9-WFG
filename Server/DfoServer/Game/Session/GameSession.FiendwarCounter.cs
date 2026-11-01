using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.Game.Titles;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal bool ReportFiendwarCounter(IFiendwarCounterStore store, FormalDungeonReceipt room,
        uint serial, FiendwarCounterReport report) => owner.Execute(() =>
    {
        // MAI在加载前冻结，CompleteLoading会正常推进epoch；以实际加载事实绑定本次房间访问。
        if (!Ready || Phase != SessionPhase.Dungeon || Selected is not { } character ||
            serial != report.Serial || !report.Matches(room) || room.Generation != owner.Generation ||
            room.SelectionEpoch != SelectionEpoch || room.Character.Id != character.Id ||
            raidCombat is not { Actor.IsCurrent: true, Battle.Kind: RaidKind.Fiendwar } raid ||
            formalDungeon is not { } dungeon || dungeon.Run.LastLoadingFact is not { } loaded ||
            loaded.Identity != room.Snapshot.Identity || loaded.Map != room.Snapshot.CurrentMap ||
            dungeon.Run.Snapshot.CurrentMap != room.Snapshot.CurrentMap ||
            dungeon.MapSeed != room.MapSeed || dungeon.Run.HasPendingDeath || dungeon.Run.HasPendingSettlement ||
            dungeon.Ground.HasPendingOperation || !CanMoveWithParty()) return false;
        var fact = AntonRaidBattle.FiendwarCounterFact.Capture(raid.Battle, raid.Actor, dungeon.Run, report);
        if (fact is null) return false;
        // 一个连接房间只有一条递增流；换房、重连及换角色均不能重用旧流。
        string stream = $"{room.Snapshot.Identity.InstanceId:N}:{owner.Generation}:{SelectionEpoch}:{room.Snapshot.Identity.Epoch}:{serial}";
        store.RecordFiendwarCounter(owner, character, fact, stream, report.Sequence);
        // 重放也允许消费上一笔已提交、但尚未来得及投影的事实；不会再次增加total。
        return true;
    });
}
