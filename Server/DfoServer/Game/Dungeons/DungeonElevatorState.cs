using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonElevatorNotice(byte Counter, byte State);
internal sealed record DungeonElevatorReceipt(long Generation, long SelectionEpoch, Guid InstanceId, DungeonMap Map,
    DungeonElevatorState Elevator, DungeonElevatorNotice? Notice = null);

// 电梯内容由地图对象组合识别；不绑定任务、职业、地下城或地图编号。
internal sealed class DungeonElevatorState(ushort controlMonsterKey)
{
    public ushort ControlMonsterKey { get; } = controlMonsterKey;
    public bool Started { get; private set; }
    public bool Completed { get; private set; }
    public byte Counter { get; private set; }
    public byte State { get; private set; } = 2;

    internal static bool HasDefinition(DungeonRoomDefinition room) => room.EventMonsterPositions.Count == 12 &&
        room.PassiveObjects.Any(actor => actor.ObjectId == 1111) && room.PassiveObjects.Any(actor => actor.ObjectId == 1113) &&
        room.SpecialObjects.Any(parent => parent.ObjectId == 1112 && parent.Children.Any(child =>
            child.Kind == "[monster]" && child.RawValues[0] == 56716));

    internal static DungeonElevatorState? Create(DungeonMonsterPlan plan) => HasDefinition(plan.Room) &&
        plan.Monsters.FirstOrDefault(actor => actor.IsSpecialChild && actor.MonsterId == 56716 &&
            actor.Source!.SpecialParent!.ObjectId == 1112) is { } control ? new(control.ObjectKey) : null;

    public bool Start()
    {
        if (Started) return false;
        Started = true;
        return true;
    }

    public DungeonElevatorNotice? Tick()
    {
        if (!Started || Completed || Counter >= 4) return null;
        return new(++Counter, 0);
    }

    public DungeonElevatorNotice? Complete(ushort monsterKey)
    {
        if (monsterKey != ControlMonsterKey || Completed) return null;
        Started = Completed = true;
        State = Counter <= 3 ? (byte)1 : (byte)2;
        return new(Counter, State);
    }
}
