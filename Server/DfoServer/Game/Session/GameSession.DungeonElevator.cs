using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public DungeonElevatorReceipt? StartDungeonElevator(FormalDungeonReceipt loaded) => owner.Execute(() =>
    {
        if (!CanPublishFormalDungeon(loaded) || Phase != SessionPhase.Dungeon ||
            formalDungeon?.Run.CurrentElevator is not { } elevator || !elevator.Start()) return null;
        return ElevatorReceipt(elevator);
    });

    public DungeonElevatorReceipt? TickDungeonElevator(DungeonElevatorReceipt scheduled) => owner.Execute(() =>
        IsCurrentElevator(scheduled) && scheduled.Elevator.Tick() is { } notice ? scheduled with { Notice = notice } : null);

    public bool CanPublishDungeonElevator(DungeonElevatorReceipt receipt) => owner.Execute(() =>
        IsCurrentElevator(receipt) && receipt.Notice is { } notice && receipt.Elevator.Counter == notice.Counter &&
        (notice.State == 0 ? !receipt.Elevator.Completed : receipt.Elevator.Completed && receipt.Elevator.State == notice.State));

    bool IsCurrentElevator(DungeonElevatorReceipt receipt) => Ready && Selected is not null && Phase == SessionPhase.Dungeon &&
        owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch && formalDungeon is { } dungeon &&
        dungeon.Run.Snapshot.Identity.InstanceId == receipt.InstanceId && dungeon.Run.Snapshot.CurrentMap == receipt.Map &&
        ReferenceEquals(dungeon.Run.CurrentElevator, receipt.Elevator);

    DungeonElevatorReceipt ElevatorReceipt(DungeonElevatorState elevator, DungeonElevatorNotice? notice = null) =>
        new(owner.Generation, SelectionEpoch, formalDungeon!.Run.Snapshot.Identity.InstanceId,
            formalDungeon.Run.Snapshot.CurrentMap, elevator, notice);
}
