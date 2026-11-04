using DfoServer.Game.Session;
using DfoServer.Game.Party;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed partial class PartyDungeonInstance
{
    AntonDungeonObjectLedger? antonObjects;
    DungeonRandomizedObjectSelection? antonObjectSelection;

    // 仅供网络构包读取；不改正式Entry的冻结来源，也不在读取时获得/消费生成许可。
    internal DungeonEntryPlan ProjectAntonObjectPlan(DungeonEntryPlan plan)
    {
        lock (gate)
            return antonObjects is not null && ReferenceEquals(plan.RandomizedObjectSelection, antonObjectSelection)
                ? plan with { RandomizedObjectSelection = antonObjects.Projection } : plan;
    }

    // Native中继成功后调用；同一源向多peer重复转发只绑定/销毁一次，不靠包字节去重。
    internal AntonObjectReport ObserveNativeObjects(GameSession source, FormalDungeonReceipt scene,
        IReadOnlyList<AntonNativeObjectSpawnBatch> spawns, IReadOnlyList<AntonNativeObjectDestruction> destructions) => Execute<AntonObjectReport>(current =>
    {
        if (CaptureAntonObjectContext(source, scene, current) is not { } context ||
            !PrepareAntonObjects(context, scene.Snapshot.CurrentMap.Position)) return new(false, false, 0);
        var room = scene.Snapshot.CurrentMap.Position;
        int accepted = 0;
        foreach (var batch in spawns)
        {
            if (batch.RoomX != room.X || batch.RoomY != room.Y) continue;
            foreach (var row in batch.Rows)
                if (row.PositionMode != 1 && row.X is { } x && row.Y is { } y && row.Z is { } z &&
                    antonObjects!.Register(context.Scope, room, row.Template, row.ObjectKind, row.ObjectKey, x, y, z)) accepted++;
        }
        foreach (var row in destructions)
            if (row.Room == room && antonObjects!.ObserveDestruction(context.Scope, room, row.ObjectKind, row.ObjectKey)) accepted++;
        bool changed = ApplyAntonObjectLast(context, scene);
        return new(accepted != 0, changed, antonObjects!.BatteryLastCount);
    });

    // 只接受当前host的明确Last声明；与Native23可先后到达，但没有21许可和销毁事实永远不能扣能量。
    internal AntonObjectReport ReportAntonObjectLast(GameSession reporter) => Execute<AntonObjectReport>(current =>
    {
        if (reporter.CurrentFormalDungeonScene() is not { } scene ||
            CaptureAntonObjectContext(reporter, scene, current) is not { } context ||
            !PrepareAntonObjects(context, scene.Snapshot.CurrentMap.Position)) return new(false, false, 0);
        bool accepted = antonObjects!.ReportBatteryLast(context.Scope, scene.Snapshot.CurrentMap.Position);
        bool changed = ApplyAntonObjectLast(context, scene);
        return new(accepted || changed, changed, antonObjects.BatteryLastCount);
    });

    AntonNativeObjectContext? CaptureAntonObjectContext(GameSession source, FormalDungeonReceipt scene, PartyMember[] current) =>
        pendingDeath is null && pendingScene is null && loaded.Count == 0 &&
        current.Any(member => member.Slot == Leader && ReferenceEquals(member.Actor.Session, source)) &&
        current.All(member => member.Actor.Session.CanMoveWithParty() &&
            member.Actor.Session.CurrentFormalDungeonScene() is { } own &&
            own.Snapshot.Identity.InstanceId == Id && own.Snapshot.CurrentMap == scene.Snapshot.CurrentMap) &&
        source.CaptureAntonNativeObjectContext(scene) is { } context && context.Scope.Run == Id ? context : null;

    bool PrepareAntonObjects(AntonNativeObjectContext context, DungeonPoint room)
    {
        if (antonObjects is null) { antonObjectSelection = context.Selection; antonObjects = new(context.Selection); }
        if (!ReferenceEquals(antonObjectSelection, context.Selection)) return false;
        antonObjects.OpenRoom(context.Scope, room);
        return true;
    }

    bool ApplyAntonObjectLast(AntonNativeObjectContext context, FormalDungeonReceipt scene)
    {
        bool changed = false;
        antonObjects!.ApplyBatteryLast(context.Scope, scene.Snapshot.CurrentMap.Position, fact =>
        {
            bool accepted = context.Battle.ConfirmAntonObjectEnergy(context.Actor, scene.Snapshot, fact, out bool applied);
            changed |= applied;
            return accepted;
        });
        return changed;
    }
}
