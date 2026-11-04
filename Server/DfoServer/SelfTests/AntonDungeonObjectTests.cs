using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AntonDungeonObjectTests
{
    public static void RunCore()
    {
        int before = Count;
        var raw = new List<DungeonRawSection>
        {
            Tag("randomized object creation"), Tag("select", 5), Tag("regenerate", 0), Tag("minimap icon", 6)
        };
        for (int index = 0; index < 8; index++) raw.AddRange(Object(index, 18865, true));
        raw.Add(Tag("/randomized object creation"));
        raw.AddRange([Tag("randomized object creation"), Tag("select", 1), Tag("regenerate", 0), Tag("minimap icon", 4)]);
        raw.AddRange(Object(9, 58530, false)); raw.Add(Tag("/randomized object creation"));
        var groups = DungeonRandomizedObjectGroups.Parse(raw, _ => "passive object");
        That(groups.Count == 2 && groups[0] is { SelectCount: 5, Regenerate: false, MinimapIcon: 6 } &&
            groups[0].Objects.Length == 8 && groups[0].Objects.All(row => row.SpawnMode == 1) &&
            groups[1].Objects.Single().SpawnMode == 0, "218随机块保留select、regenerate、icon及显式被动物件模式");
        var selected = DungeonRandomizedObjectSelection.Create(groups, _ => 0);
        That(selected.Groups[0].Objects.Length == 5 && selected.Groups[1].Objects.Single().ObjectId == 58530 &&
            selected.MinimapGroups[0].Length == 5 && selected.MinimapGroups[1].Length == 1,
            "28图标和29生成由同一份五选八加独立单选结果投影");
        That(selected.Groups[0].Objects.Distinct().Count() == 5, "无放回选择不会让同一电池成为多份许可");
        bool invalidRoll = false;
        try { DungeonRandomizedObjectSelection.Create(groups, max => max); }
        catch (InvalidOperationException) { invalidRoll = true; }
        That(invalidRoll, "随机选择器越界时拒绝冻结非法候选");
        That(DungeonRandomizedObjectGroups.Parse(raw.Take(raw.Count - 1).ToArray(), _ => "passive object").Count == 0,
            "缺少分组终点的资源不产生生成许可");
        var emptyFirst = DungeonRandomizedObjectSelection.Create([
            new(1, false, 6, []), new(1, false, 4, [new(new(2, 2), 58530, "monster", 459, 256)])]);
        That(emptyFirst.MinimapGroups.Length == 2 && emptyFirst.MinimapGroups[0].IsEmpty &&
            emptyFirst.MinimapGroups[1].Single() == new DungeonPoint(2, 2), "空图标组保留后续组索引");
        var map = new DungeonMap("map", new(2, 2), 19110, "map/anton/test.map");
        var maze = new DungeonMaze(3, 4, 5, "", [map.Position], [map.Position], [map], []);
        var plan = new DungeonEntryPlan(1, 0, 218, 0, 0, 2, maze, map, map)
        { RandomizedObjectSelection = emptyFirst, IsPractice = true, IsCosmo = true };
        byte[] info = DungeonPackets.Information(plan);
        That(info.Length == 40 && info[12] == 1 && info.AsSpan(13, 5).SequenceEqual(new byte[] { 2, 0, 1, 2, 2 }) &&
            BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(18)) == 1 && info[22] == 11 &&
            BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(24)) == uint.MaxValue,
            "NOTI28在offset13插入含空组的坐标列表，深渊开关和后续seed随完整尾部后移");
        var passiveSelection = DungeonRandomizedObjectSelection.Create([
            new(1, false, 6, [new(map.Position, 18865, "monster", 459, 256) { SpawnMode = 1 }])]);
        byte[] start = DungeonPackets.StartMap(plan with { RandomizedObjectSelection = passiveSelection }, 0x12345678);
        That(start.Length == 44 && start[21] == 1 && start[22] == 1 &&
            start.AsSpan(23, 20).SequenceEqual(new byte[] {
                203, 1, 0, 0, 0, 1, 0, 0, 177, 73, 0, 0, 100, 0, 0, 0, 1, 0, 0, 0 }) && start[43] == 255,
            "NOTI29被动小电池按资源坐标、模板、阵营和mode1写五u32，不混用实例键");

        var ledger = new AntonDungeonObjectLedger(selected);
        var scope = new AntonDungeonObjectScope(Guid.NewGuid(), Guid.NewGuid(), 3, 9, 10, 4);
        var row = selected.Groups[0].Objects[0]; var room = row.Map;
        That(ledger.OpenRoom(scope, room).Single() == row, "冻结房间许可只包含本格已选资源");
        That(!ledger.Register(scope, room, 18865, 33, 23, 0, 0, 0) &&
            !ledger.Register(scope with { HostEpoch = 5 }, room, 18865, 33, 23, 459, 256, 0) &&
            !ledger.Register(scope, new(99, 99), 18865, 33, 23, 459, 256, 0),
            "自报模板不能绕过坐标、host会话代和房间许可");
        That(ledger.Register(scope, room, 18865, 0x111, 23, 459, 256, 0) &&
            ledger.Register(scope, room, 18865, 0x111, 23, 459, 256, 0) &&
            !ledger.Register(scope, room, 18865, 0x111, 24, 459, 256, 0),
            "Native真实kind/key绑定许可，向多peer转发幂等且不能另造第二实例");
        That(!ledger.TryDestroy(scope, room, 0x21, 23, out _) &&
            !ledger.TryDestroy(scope with { Generation = 2 }, room, 0x111, 23, out _),
            "对象类型与副本代不匹配的销毁不消耗许可");
        That(ledger.TryDestroy(scope, room, 0x111, 23, out var fact) &&
            fact is { Resource.ObjectId: 18865, ObjectKind: 0x111, ObjectKey: 23 } &&
            !ledger.TryDestroy(scope, room, 0x111, 23, out _) &&
            !ledger.Register(scope, room, 18865, 0x111, 23, 459, 256, 0),
            "真实销毁只提交一次并阻止相同身份重新领许可");
        That(ledger.OpenRoom(scope with { RoomEpoch = 10 }, room).IsEmpty &&
            !ledger.MinimapGroups[0].Contains(room), "regenerate0的电池在回访和后续图标投影均已消耗");

        var regenerating = new AntonDungeonObjectLedger(DungeonRandomizedObjectSelection.Create([
            new(1, true, 6, [row])]));
        regenerating.OpenRoom(scope, room);
        That(regenerating.Register(scope, room, 18865, 1, 2, 459, 256, 0) &&
            regenerating.TryDestroy(scope, room, 1, 2, out _) && regenerating.OpenRoom(scope, room).IsEmpty &&
            regenerating.Projection.ForRoom(room).IsEmpty && regenerating.MinimapGroups[0].IsEmpty &&
            regenerating.OpenRoom(scope with { RoomEpoch = 10 }, room).Length == 1,
            "regenerate1只在新房间代重新生成，同代投影隐藏且不在同代重报时补许可");
        var revisit = scope with { RoomEpoch = 10 };
        That(!regenerating.Register(revisit, room, 18865, 1, 2, 459, 256, 0) &&
            !regenerating.TryDestroy(revisit, room, 1, 2, out _) &&
            regenerating.Register(revisit, room, 18865, 1, 3, 459, 256, 0) &&
            regenerating.Register(revisit, room, 18865, 1, 3, 459, 256, 0) &&
            regenerating.TryDestroy(revisit, room, 1, 3, out _) &&
            !regenerating.TryDestroy(revisit, room, 1, 3, out _),
            "同房回访拒绝旧21加23重放，新实例键合法生成且多peer转发仍幂等");
        BatteryLastContract(row, scope);
        AntonConstructorTests.RunCore();
        Console.WriteLine($"PASS {Count - before} 安徒恩冻结对象选择与实例许可断言");
    }

    static void BatteryLastContract(DungeonRandomizedObject row, AntonDungeonObjectScope scope)
    {
        var clock = new ObjectClock();
        var selection = DungeonRandomizedObjectSelection.Create([new(2, false, 6,
            [row, row with { PositionX = row.PositionX + 1 }])]);
        var ledger = new AntonDungeonObjectLedger(selection, clock);
        var room = row.Map;
        ledger.OpenRoom(scope, room);
        int commits = 0;
        var eventIds = new List<long>();
        bool Commit(AntonDungeonObjectDestructionFact fact) { commits++; eventIds.Add(fact.EventId); return true; }
        That(ledger.ReportBatteryLast(scope, room) && ledger.ApplyBatteryLast(scope, room, Commit) == 0 && commits == 0,
            "只有Last声明不产生能量变化");
        That(ledger.ObserveDestruction(scope, room, 7, 1) && ledger.PendingDestructionCount == 1 &&
            ledger.ApplyBatteryLast(scope, room, Commit) == 0 &&
            ledger.Register(scope, room, 18865, 7, 1, row.PositionX, row.PositionY, 0) &&
            ledger.PendingDestructionCount == 0 && ledger.ApplyBatteryLast(scope, room, Commit) == 1 &&
            commits == 1 && ledger.BatteryLastCount == 1,
            "Last及UDP23先于21时有界等待，21批准真实身份后双事实只提交一次");
        That(!ledger.ObserveDestruction(scope, room, 7, 1) && ledger.ReportBatteryLast(scope, room) &&
            !ledger.ReportBatteryLast(scope, room) && ledger.PendingLastCount == 1 &&
            ledger.ApplyBatteryLast(scope, room, Commit) == 0 && commits == 1,
            "重复23及Last不能超出当前冻结许可和真实销毁总量");
        That(ledger.Register(scope, room, 18865, 7, 2, row.PositionX + 1, row.PositionY, 0) &&
            ledger.ObserveDestruction(scope, room, 7, 2) && ledger.ApplyBatteryLast(scope, room, _ => false) == 0 &&
            ledger.PendingLastCount == 1 && ledger.BatteryLastCount == 1 &&
            ledger.ApplyBatteryLast(scope, room, Commit) == 1 && commits == 2 &&
            eventIds.Distinct().Count() == 2 && eventIds.SequenceEqual(new[] {
                AntonDungeonObjectLedger.EventId(0, 0), AntonDungeonObjectLedger.EventId(0, 1) }) &&
            !ledger.ReportBatteryLast(scope, room) && ledger.BatteryLastCount == 2,
            "业务拒绝保留原配对资格，稳定事件由冻结组和选择序号产生且每许可只消费一次");

        var later = new AntonDungeonObjectLedger(selection, clock);
        later.OpenRoom(scope, room);
        That(later.Register(scope, room, 18865, 8, 1, row.PositionX, row.PositionY, 0) &&
            later.ObserveDestruction(scope, room, 8, 1) && later.ApplyBatteryLast(scope, room, Commit) == 0 &&
            later.ReportBatteryLast(scope, room) && later.ApplyBatteryLast(scope, room, Commit) == 1 && commits == 3,
            "21和23先到、TCP Last后到的顺序同样只提交一次");

        var bounded = new AntonDungeonObjectLedger(selection, clock);
        bounded.OpenRoom(scope, room);
        That(bounded.ObserveDestruction(scope, room, 9, 1) && bounded.ObserveDestruction(scope, room, 9, 2) &&
            !bounded.ObserveDestruction(scope, room, 9, 3) && bounded.PendingDestructionCount == 2 &&
            bounded.ReportBatteryLast(scope, room) && bounded.ReportBatteryLast(scope, room) &&
            !bounded.ReportBatteryLast(scope, room), "早到23和Last等待都以当前格许可数为容量上限");
        clock.Advance(AntonDungeonObjectLedger.PairWindow + TimeSpan.FromSeconds(1));
        That(bounded.Register(scope, room, 18865, 9, 1, row.PositionX, row.PositionY, 0) &&
            bounded.PendingDestructionCount == 0 && bounded.PendingLastCount == 0 &&
            bounded.ApplyBatteryLast(scope, room, Commit) == 0 && commits == 3,
            "超时早到23和Last不会在后续21到达时借用旧声明");
        bounded.ReportBatteryLast(scope, room);
        var next = scope with { HostEpoch = scope.HostEpoch + 1, RoomEpoch = scope.RoomEpoch + 1 };
        bounded.OpenRoom(next, room);
        That(bounded.PendingLastCount == 0 && bounded.PendingDestructionCount == 0 &&
            !bounded.ReportBatteryLast(scope, room) && !bounded.ObserveDestruction(scope, room, 9, 2) &&
            bounded.ApplyBatteryLast(scope, room, Commit) == 0 && commits == 3,
            "换房及换host清空所有等待，旧scope不能把声明带入新对象");
        var regenerated = new AntonDungeonObjectLedger(DungeonRandomizedObjectSelection.Create([new(1, true, 6, [row])]), clock);
        regenerated.OpenRoom(scope, room);
        That(!regenerated.ReportBatteryLast(scope, room), "当前100减量合同只开放资源声明不再生的18865");
    }

    sealed class ObjectClock : TimeProvider
    {
        long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan elapsed) => timestamp += elapsed.Ticks;
    }

    static DungeonRawSection Tag(string tag, params int[] values) => new(tag,
        values.Select(value => new PvfToken(0, value)).ToArray());
    static DungeonRawSection[] Object(int mapX, int template, bool passive) => [Tag("object"),
        ..(passive ? new DungeonRawSection[] { new("object type", [new PvfToken(3, 1)]) } : []),
        Tag("map", mapX, 0), Tag("index", template), Tag("team"), Tag("monster"), Tag("pos", 459, 256), Tag("/object")];
}
