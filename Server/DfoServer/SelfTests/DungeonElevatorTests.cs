using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonElevatorTests
{
    public static void Run()
    {
        int before = Count;
        foreach (byte stopAt in new byte[] { 0, 1, 3, 4 })
        {
            var state = new DungeonElevatorState(17);
            That(state.Tick() is null && state.Complete(18) is null && !state.Started, "电梯未加载和其他目标退场不推进");
            That(state.Start() && !state.Start(), "重复加载只开始一次计时");
            for (byte counter = 1; counter <= stopAt; counter++)
                That(state.Tick() == new DungeonElevatorNotice(counter, 0), "每阶段只发布递增计数与状态0");
            if (stopAt == 4) That(state.Tick() is null && !state.Completed, "四次计时结束不能代替控制怪退场");
            var notice = state.Complete(17)!;
            That(notice == new DungeonElevatorNotice(stopAt, stopAt <= 3 ? (byte)1 : (byte)2) &&
                state.Completed && state.Tick() is null && state.Complete(17) is null, "退场按实际计数选择状态，旧计时与重复退场停止");
            That(DungeonPackets.Elevator(notice).SequenceEqual(new byte[] { notice.Counter, notice.State }), "电梯通知完整两字节");
        }
        var start = new DungeonMap("map", new(0, 0), 987001, "map/elevator-fixture.map");
        var boss = new DungeonMap("boss", new(1, 0), 987002, "map/boss-fixture.map");
        var child = new DungeonRoomSpecialChild(0, "[monster]", [56716, 49, 5, 5, -1], "monster/control.mob");
        var room = new DungeonRoomDefinition(start, [], [new(1111, [1111, 0, 4, 138], null), new(1113, [1113, 491, 77, 0], null)], [])
        {
            SpecialObjects = [new(0, 1112, [1112, 200, 197, 0, 1], "passiveobject/mapobject/obstacle/elevatorsummon.obj", [child])],
            EventMonsterPositions = [330, 93, 0, 357, 247, 0, 566, 175, 0, 672, 237, 0]
        };
        That(DungeonElevatorState.HasDefinition(room) && !DungeonElevatorState.HasDefinition(room with { EventMonsterPositions = [] }) &&
            !DungeonElevatorState.HasDefinition(room with { PassiveObjects = [room.PassiveObjects[0]] }), "电梯按完整内容结构识别，不匹配不完整机关");
        var plan = DungeonMonsterPlan.FromRoom(room, 99, 10); // 剧情默认路径也必须生成父子关联。
        That(plan.SpecialObjects.Count == 1 && plan.Monsters.Single() is { IsSpecialChild: true, IsBlocking: false, MonsterId: 56716 } &&
            plan.SpecialObjects[0].ObjectKey != plan.Monsters[0].ObjectKey, "剧情电梯的父对象与控制子怪使用独立key");
        var bossPlan = DungeonMonsterPlan.FromRoom(new(boss, [], [], []), 99, 20);
        var maze = new DungeonMaze(0, 2, 1, "fixture", [start.Position], [boss.Position], [start, boss], []);
        var entry = new DungeonEntryPlan(99, 1, 987, 1234, 0, 0, maze, start, boss);
        var route = new DungeonFixedRoute([plan, bossPlan], [new(start.Position, boss.Position)]);
        var authority = new Authority();
        That(SinglePlayerDungeonRun.TryCreate(entry, 1, 100, route, authority, out var run, out _), "任意任务与地图编号的电梯实例可建立");
        run!.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        var elevator = run.CurrentElevator!;
        That(elevator.Start() && run.Move(run.Snapshot.Identity, Guid.NewGuid(), boss.Position).Outcome == DungeonRunOutcome.Rejected,
            "未结束的电梯阻止提前换图");
        That(run.RetireSpecialChild(authority.Death(run.Snapshot.Identity, elevator.ControlMonsterKey)) == DungeonRunOutcome.Accepted &&
            elevator.Complete(elevator.ControlMonsterKey) is not null && run.ClearFact is null, "控制怪退场不伪造击杀奖励和清图事实");
        That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), boss.Position).Outcome == DungeonRunOutcome.Accepted &&
            run.CurrentElevator is null, "结束后沿合法连接换图，目标房间不继承电梯状态");
        Console.WriteLine($"PASS {Count - before} 电梯阶段、特殊actor与换图断言（合成）");
    }

    sealed class Authority : DungeonCombatAuthority
    {
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity, ushort key) => ConfirmDeath(identity, Guid.NewGuid(), key, 1);
    }
}
