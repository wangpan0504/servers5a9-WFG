using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer;

internal static class DungeonGroundStateTests
{
    public static void RunCore()
    {
        int checks = Count;
        var fixture = Fixture();
        var state = fixture.Ground;
        var run = fixture.Run;
        var reservations = fixture.Reservations;
        That(state.HasReservation(100) && state.HasReservation(102) && !state.HasReservation(999),
            "只有预规划资格入口，不给无声明模板默认掉落");
        RejectData(() => new DungeonGroundState(run, fixture.Plans, [reservations[0] with { GroundKey = 99 }]), "地面key不能碰角色");
        RejectData(() => new DungeonGroundState(run, fixture.Plans, [reservations[0] with { GroundKey = 102 }]), "地面key不能碰未来房怪物");
        RejectData(() => new DungeonGroundState(run, fixture.Plans, [reservations[0] with { GroundKey = 0 }]), "地面零key拒绝");
        RejectData(() => new DungeonGroundState(run, fixture.Plans, [reservations[0], reservations[1] with { GroundKey = reservations[0].GroundKey }]),
            "不同怪物地面key不能重复");
        RejectData(() => new DungeonGroundState(run, fixture.Plans, [reservations[0], reservations[0] with { GroundKey = 900 }]),
            "单怪不能预留第二件");
        RejectData(() => new DungeonGroundState(run, fixture.Plans, [reservations[0] with { Map = fixture.Plans[1].Room.Map }]),
            "预留房间不能换成另一张图");
        RejectData(() => new DungeonGroundState(run, fixture.Plans, [reservations[0] with { MonsterKey = 999 }]), "未知怪物预留拒绝");
        RejectData(() => new DungeonGroundState(run, [fixture.Plans[0], fixture.Plans[0]], []), "全路线怪物key重复拒绝");
        reservations[0] = reservations[0] with { GroundKey = 999 };

        Load(run);
        var death = fixture.Authority.Death(run.Snapshot.Identity, 100, 1);
        var prepared = run.PrepareConfirmedDeath(death).Pending!;
        int draws = 0;
        var core = Core();
        ItemCore? Factory()
        {
            draws++;
            return core;
        }
        var clonedPrepared = new PreparedDungeonDeath(prepared.Death, prepared.Monster, prepared.Result);
        RejectState(() => state.GetOrFreeze(clonedPrepared, Factory), "复制Prepared不能触发factory");
        That(draws == 0, "拒绝发生在调用factory之前");
        var frozen = state.GetOrFreeze(prepared, Factory);
        var ground = frozen.Ground!;
        var originalSnapshot = state.Snapshot;
        That(draws == 1 && ReferenceEquals(state.GetOrFreeze(prepared, Factory), frozen) && draws == 1,
            "精确准备重复冻结只调用factory一次");
        That(ReferenceEquals(ground.Core, core) && ground.Core == core && ground.DeathEventId == death.EventId &&
            ground.DeathIdentity == death.Identity && ground.Reservation.GroundKey == 400 && ground.Reservation.RecordSlot == -17 &&
            ground.Reservation.X == 500 && ground.Reservation.Y == 200, "完整Core、原死亡身份及显式位置槽值冻结，源数组替换不污染");
        That(originalSnapshot.Items.Single().Status == DungeonGroundStatus.Frozen &&
            !state.PublishCommitted(prepared, frozen) && state.FindAvailable(run.Snapshot.Identity, 400) is null &&
            state.PreparePickup(run.Snapshot.Identity, ground) is null, "SQL未定且Run未确认之前不可拾取");

        That(run.CancelUncommittedDeath(prepared), "明确SQL回滚取消Run准备");
        RejectState(() => state.GetOrFreeze(prepared, Factory), "撤销的旧准备不能借缓存冻结");
        var retried = run.PrepareConfirmedDeath(death).Pending!;
        That(!ReferenceEquals(retried, prepared) && ReferenceEquals(state.GetOrFreeze(retried, Factory), frozen) && draws == 1,
            "回滚重新Prepare同一exact死亡仍复用完整冻结结果");
        That(run.ConfirmCommittedDeath(retried).Outcome == DungeonRunOutcome.Accepted &&
            !state.PublishCommitted(prepared, frozen) && !state.PublishCommitted(clonedPrepared, frozen),
            "只接实际已确认Prepared，旧回滚和复制候选不能发布");
        var clonedFrozen = new FrozenDungeonGroundDeath(frozen.Death, frozen.CommittedIdentity, frozen.Map, ground);
        That(!state.PublishCommitted(retried, clonedFrozen) && state.PublishCommitted(retried, frozen) &&
            !state.PublishCommitted(retried, frozen), "exact冻结对象提交后只发布一次");
        That(originalSnapshot.Items.Single().Status == DungeonGroundStatus.Frozen &&
            ReferenceEquals(state.FindAvailable(run.Snapshot.Identity, 400), ground), "旧快照保持不可变，新快照取得原地面对象");
        That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), fixture.Plans[1].Room.Map.Position).Outcome == DungeonRunOutcome.Rejected &&
            ReferenceEquals(state.FindAvailable(run.Snapshot.Identity, 400), ground), "未清房拒绝过房不会清掉仍合法的地面物品");

        var identity = run.Snapshot.Identity;
        foreach (var wrong in new[]
        {
            identity with { CharacterId = 98 }, identity with { QuestId = 3146 }, identity with { TaskAttempt = 2 },
            identity with { InstanceId = Guid.NewGuid() }, identity with { Epoch = identity.Epoch - 1 }, identity with { Epoch = identity.Epoch + 1 }
        })
            That(state.FindAvailable(wrong, 400) is null && state.PreparePickup(wrong, ground) is null, "拾取完整身份不匹配拒绝");
        That(state.PreparePickup(identity, ground with { Content = new(core with { Attr = 2 }) }) is null &&
            state.PreparePickup(identity, ground with { }) is null, "即使字段相等的Ground复制也不能更换完整Core或原对象");
        var availableSnapshot = state.Snapshot;
        var pickup = state.PreparePickup(identity, ground) ?? throw new InvalidOperationException("FAIL 合法地面拾取未生成准备");
        That(ReferenceEquals(pickup.Ground, ground) && state.HasPendingPickup && !state.ExpireRun() &&
            state.FindAvailable(identity, 400) is null && ReferenceEquals(state.PreparePickup(identity, ground), pickup),
            "未知SQL结果保留同一拾取准备并阻止出房，不重复规划资产");
        var clonedPickup = new DungeonGroundPickupPreparation(pickup.Identity, pickup.Ground);
        That(!state.ConfirmPickup(clonedPickup) && !state.CancelPickup(clonedPickup), "复制拾取准备不能确认或释放原准备");
        That(state.CancelPickup(pickup) && !state.HasPendingPickup && !state.CancelPickup(pickup) &&
            ReferenceEquals(state.FindAvailable(identity, 400), ground), "明确拾取SQL回滚只恢复原地面对象");
        var pickupRetry = state.PreparePickup(identity, ground)!;
        That(!ReferenceEquals(pickupRetry, pickup) && !state.ConfirmPickup(pickup) && !state.CancelPickup(pickup),
            "重试后的旧准备不能确认或撤销新准备");
        That(state.ConfirmPickup(pickupRetry) && !state.ConfirmPickup(pickupRetry) && !state.HasPendingPickup &&
            state.FindAvailable(identity, 400) is null && state.PreparePickup(identity, ground) is null &&
            availableSnapshot.Items.Single().Status == DungeonGroundStatus.Available, "提交确认只消耗一次，重复拾取不输出资产且旧快照不变");

        var missDeath = fixture.Authority.Death(identity, 101, 2);
        var missPrepared = run.PrepareConfirmedDeath(missDeath).Pending!;
        int misses = 0;
        ItemCore? Miss()
        {
            misses++;
            return null;
        }
        var miss = state.GetOrFreeze(missPrepared, Miss);
        That(miss.Ground is null && ReferenceEquals(state.GetOrFreeze(missPrepared, Miss), miss) && misses == 1,
            "未命中也冻结，重试不能从零结果变成掉落");
        run.CancelUncommittedDeath(missPrepared);
        var missRetry = run.PrepareConfirmedDeath(missDeath).Pending!;
        That(ReferenceEquals(state.GetOrFreeze(missRetry, Factory), miss) && draws == 1 && misses == 1,
            "回滚后未命中不会调用新的命中factory");
        run.ConfirmCommittedDeath(missRetry);
        That(state.PublishCommitted(missRetry, miss) && state.Snapshot.Items.Length == 1 && state.FindAvailable(identity, 401) is null,
            "明确提交的未命中不建立空物品实体");
        That(!state.ExpireRun() && run.Move(identity, Guid.NewGuid(), fixture.Plans[1].Room.Map.Position).Outcome == DungeonRunOutcome.Accepted &&
            state.Snapshot.Items.IsEmpty, "无待拾取时允许出房，新房只暴露本房对象");

        var other = Fixture();
        Load(other.Run);
        var otherPrepared = other.Run.PrepareConfirmedDeath(other.Authority.Death(other.Run.Snapshot.Identity, 100, 1)).Pending!;
        RejectState(() => state.GetOrFreeze(otherPrepared, Factory), "另一Run的精确候选也不能冻结到本State");
        var otherFrozen = other.Ground.GetOrFreeze(otherPrepared, Core);
        other.Run.ConfirmCommittedDeath(otherPrepared);
        other.Ground.PublishCommitted(otherPrepared, otherFrozen);
        That(state.PreparePickup(run.Snapshot.Identity, otherFrozen.Ground!) is null &&
            !other.Ground.ConfirmPickup(pickupRetry) && !other.Ground.CancelPickup(pickupRetry), "同数值key的跨实例原对象与准备均隔离");

        var noReservation = Fixture();
        var limited = new DungeonGroundState(noReservation.Run, noReservation.Plans, [noReservation.Reservations[0]]);
        Load(noReservation.Run);
        var unsupported = noReservation.Run.PrepareConfirmedDeath(noReservation.Authority.Death(noReservation.Run.Snapshot.Identity, 101, 1)).Pending!;
        RejectState(() => limited.GetOrFreeze(unsupported, Factory), "未获声明的怪物不隐含调用factory");
        That(draws == 1, "无预留拒绝不消费抽样");

        var expired = Fixture(withMiddle: true);
        Load(expired.Run);
        var expiredGround = FreezeCommit(expired, 100, 1, Core).Ground!;
        FreezeCommit(expired, 101, 2, Core);
        var expiredSnapshot = expired.Ground.Snapshot;
        That(expired.Run.Move(expired.Run.Snapshot.Identity, Guid.NewGuid(), expired.Plans[1].Room.Map.Position).Outcome == DungeonRunOutcome.Accepted &&
            expired.Ground.Snapshot.Items.IsEmpty && expired.Ground.FindAvailable(expired.Run.Snapshot.Identity, 400) is null,
            "加载与另一个房间隐藏旧房对象，不能跨房拾取");
        Load(expired.Run);
        FreezeCommit(expired, 102, 3, () => null);
        expired.Run.Move(expired.Run.Snapshot.Identity, Guid.NewGuid(), expired.Plans[0].Room.Map.Position);
        Load(expired.Run);
        That(expired.Ground.Snapshot.Items.All(item => item.Status == DungeonGroundStatus.Available) &&
            ReferenceEquals(expired.Ground.FindAvailable(expired.Run.Snapshot.Identity, 400), expiredGround) &&
            expiredSnapshot.Items.All(item => item.Status == DungeonGroundStatus.Available),
            "回访同地图保留原完整物品与key，历史快照不变，不重抽或重建对象");
        That(expired.Ground.FindAvailable(expiredGround.DeathIdentity, 400) is null &&
            expired.Ground.PreparePickup(expiredGround.DeathIdentity, expiredGround) is null,
            "回访仍要求当前加载epoch，旧死亡身份不能直接取得拾取资格");
        var revisitedPickup = expired.Ground.PreparePickup(expired.Run.Snapshot.Identity, expiredGround)!;
        That(expired.Ground.ConfirmPickup(revisitedPickup), "回访原房间可以确认一次原物品拾取");
        expired.Run.Move(expired.Run.Snapshot.Identity, Guid.NewGuid(), expired.Plans[1].Room.Map.Position);
        Load(expired.Run);
        expired.Run.Move(expired.Run.Snapshot.Identity, Guid.NewGuid(), expired.Plans[0].Room.Map.Position);
        Load(expired.Run);
        That(expired.Ground.FindAvailable(expired.Run.Snapshot.Identity, 400) is null &&
            expired.Ground.PreparePickup(expired.Run.Snapshot.Identity, expiredGround) is null &&
            expired.Ground.Snapshot.Items.Single(item => item.Ground.Reservation.GroundKey == 400).Status == DungeonGroundStatus.Picked,
            "再次回访不恢复已拾取物品，其他未拾取对象保持原状态");

        var returned = Fixture();
        Load(returned.Run);
        var returnedGround = FreezeCommit(returned, 100, 1, Core).Ground!;
        returned.Run.Return(returned.Run.Snapshot.Identity, Guid.NewGuid());
        That(returned.Ground.Snapshot.Items.Single().Status == DungeonGroundStatus.Expired &&
            returned.Ground.PreparePickup(returned.Run.Snapshot.Identity, returnedGround) is null, "退出使未拾取对象过期");

        var settling = Fixture();
        Load(settling.Run);
        FreezeCommit(settling, 100, 1, () => null);
        FreezeCommit(settling, 101, 2, () => null);
        settling.Run.Move(settling.Run.Snapshot.Identity, Guid.NewGuid(), settling.Plans[1].Room.Map.Position);
        Load(settling.Run);
        var bossIdentity = settling.Run.Snapshot.Identity;
        var bossDrop = FreezeCommit(settling, 102, 3, Core).Ground!;
        var settlementIdentity = settling.Run.Snapshot.Identity;
        That(settling.Run.Phase == SinglePlayerDungeonPhase.Settlement && settlementIdentity.Epoch == bossIdentity.Epoch + 1 &&
            bossDrop.DeathIdentity == bossIdentity && settling.Ground.FindAvailable(bossIdentity, 402) is null &&
            ReferenceEquals(settling.Ground.FindAvailable(settlementIdentity, 402), bossDrop), "同房Settlement合法epoch推进保留物品，拾取必须用当前完整身份");
        That(settling.Ground.ConfirmPickup(settling.Ground.PreparePickup(settlementIdentity, bossDrop)!), "Settlement仍可完成本房拾取");

        var failed = Fixture();
        Load(failed.Run);
        var failedPrepared = failed.Run.PrepareConfirmedDeath(failed.Authority.Death(failed.Run.Snapshot.Identity, 100, 1)).Pending!;
        int failures = 0;
        RejectData(() => failed.Ground.GetOrFreeze(failedPrepared, () =>
        {
            failures++;
            return Core() with { Value = 2 };
        }),
            "错误数量完整Core拒绝，不发布部分地面对象");
        RejectState(() => failed.Ground.GetOrFreeze(failedPrepared, () =>
        {
            failures++;
            return Core();
        }), "失败尝试不能重抽");
        That(failures == 1 && failed.Ground.Snapshot.Items.IsEmpty, "factory失败保留有界拒绝状态，未创建可拾取对象");
        MultipleGroundObjects();
        Console.WriteLine($"PASS {Count - checks} 地面冻结与拾取生命周期纯合成断言");

        void MultipleGroundObjects()
        {
            var many = Fixture(withMiddle: true);
            var extra = many.Reservations[0];
            DungeonGroundReservation[] reserved = [.. many.Reservations,
                extra with { GroundKey = 700, Ordinal = 1 }, extra with { GroundKey = 701, Ordinal = 2 },
                extra with { GroundKey = 702, Ordinal = 3 }];
            var owner = new DungeonGroundState(many.Run, many.Plans, reserved);
            Load(many.Run);
            var eventFact = many.Authority.Death(many.Run.Snapshot.Identity, 100, 1);
            var preparation = many.Run.PrepareConfirmedDeath(eventFact).Pending!;
            DungeonGroundContent[] contents = [new(Core()), new(new ItemCore(2000, ItemKind.Material, 7)),
                new(new ItemCore(3000, ItemKind.Equipment, 0) { Durability = 13 }), new(null, 77)];
            int calls = 0;
            var frozenMany = owner.GetOrFreezeAssets(preparation, () => { calls++; return contents; });
            contents[1] = new(contents[1].Item! with { Value = 99 });
            That(calls == 1 && frozenMany.Grounds.Select(item => item.Reservation.GroundKey).SequenceEqual(new uint[] { 400, 700, 701, 702 }) &&
                frozenMany.Grounds[1].Core.Count == 7 && frozenMany.Grounds[2].Core.Value == 0 &&
                frozenMany.Grounds[3].IsGold && frozenMany.Grounds[3].GoldAmount == 77 &&
                owner.Snapshot.Items.All(item => item.Status == DungeonGroundStatus.Frozen),
                "同一死亡冻结完整消耗品、材料数量、零品级装备及独立金币，源列表修改不污染集合");
            RejectState(() => { _ = frozenMany.Ground; }, "单件仓储不能悄悄截断多件冻结结果");
            That(many.Run.CancelUncommittedDeath(preparation), "多件死亡明确回滚允许撤销Run准备");
            var retry = many.Run.PrepareConfirmedDeath(eventFact).Pending!;
            RejectState(() => owner.GetOrFreezeAssets(retry, () => { calls++; return []; }, "改换后的规则来源"),
                "同怪已冻结集合拒绝改换资源来源，不能以重新准备计划掩盖原结果");
            That(ReferenceEquals(owner.GetOrFreezeAssets(retry, () => { calls++; return []; }), frozenMany) && calls == 1,
                "多件掉落回滚后复用原完整集合，不重抽或缩为空结果");
            many.Run.ConfirmCommittedDeath(retry);
            That(owner.PublishCommitted(retry, frozenMany) && frozenMany.Grounds.All(item =>
                ReferenceEquals(owner.FindAvailable(many.Run.Snapshot.Identity, item.Reservation.GroundKey), item)),
                "同一明确提交后发布全部独立地面key，不仅发布首件");
            var identity = many.Run.Snapshot.Identity;
            var first = owner.PreparePickup(identity, frozenMany.Grounds[0])!;
            That(owner.PreparePickup(identity, frozenMany.Grounds[1]) is null && owner.CancelPickup(first) &&
                frozenMany.Grounds.All(item => owner.FindAvailable(identity, item.Reservation.GroundKey) is not null),
                "一件拾取未定时拒绝另一件准备，回滚不消耗同死亡的其他资产");
            That(owner.ConfirmPickup(owner.PreparePickup(identity, frozenMany.Grounds[0])!) &&
                owner.FindAvailable(identity, 400) is null && owner.FindAvailable(identity, 700) is not null &&
                owner.FindAvailable(identity, 701) is not null, "确认一件只消费该key，其余材料和装备仍可独立拾取");
            var remaining = many.Run.PrepareConfirmedDeath(many.Authority.Death(identity, 101, 2)).Pending!;
            var empty = owner.GetOrFreezeMany(remaining, () => []);
            many.Run.ConfirmCommittedDeath(remaining); owner.PublishCommitted(remaining, empty);
            many.Run.Move(many.Run.Snapshot.Identity, Guid.NewGuid(), many.Plans[1].Room.Map.Position); Load(many.Run);
            That(owner.FindAvailable(many.Run.Snapshot.Identity, 700) is null, "多件旧房key也不能在其他房间拾取");
            var middle = many.Run.PrepareConfirmedDeath(many.Authority.Death(many.Run.Snapshot.Identity, 102, 3)).Pending!;
            var middleEmpty = owner.GetOrFreezeMany(middle, () => []);
            many.Run.ConfirmCommittedDeath(middle); owner.PublishCommitted(middle, middleEmpty);
            many.Run.Move(many.Run.Snapshot.Identity, Guid.NewGuid(), many.Plans[0].Room.Map.Position); Load(many.Run);
            That(owner.FindAvailable(many.Run.Snapshot.Identity, 400) is null &&
                ReferenceEquals(owner.FindAvailable(many.Run.Snapshot.Identity, 700), frozenMany.Grounds[1]) &&
                owner.ConfirmPickup(owner.PreparePickup(many.Run.Snapshot.Identity, frozenMany.Grounds[1])!) &&
                owner.FindAvailable(many.Run.Snapshot.Identity, 701) is not null,
                "回访保持各件自己的已拾取状态，原材料数量和装备不会重建");
            var gold = frozenMany.Grounds[3];
            RejectState(() => { _ = gold.Core; }, "金币不是伪造itemId0的实体Core");
            That(owner.ConfirmPickup(owner.PreparePickup(many.Run.Snapshot.Identity, gold)!) &&
                owner.FindAvailable(many.Run.Snapshot.Identity, 702) is null,
                "同死亡金币使用独立key确认，不消费尚未拾取装备");
            RejectData(() => new DungeonGroundContent(null, 0).Validate(), "零金币不能占据有效地面对象");
            RejectData(() => new DungeonGroundContent(Core(), 1).Validate(), "物品与金币来源不能混合为一个对象");
        }

        void RejectData(Action action, string name) => Reject<InvalidDataException>(action, name);
        void RejectState(Action action, string name) => Reject<InvalidOperationException>(action, name);
    }

    static FrozenDungeonGroundDeath FreezeCommit(GroundFixture fixture, ushort key, long sequence, Func<ItemCore?> factory)
    {
        var prepared = fixture.Run.PrepareConfirmedDeath(fixture.Authority.Death(fixture.Run.Snapshot.Identity, key, sequence)).Pending!;
        var frozen = fixture.Ground.GetOrFreeze(prepared, factory);
        if (fixture.Run.ConfirmCommittedDeath(prepared).Outcome != DungeonRunOutcome.Accepted || !fixture.Ground.PublishCommitted(prepared, frozen))
            throw new InvalidOperationException("FAIL 合成地面事实无法确认发布");
        return frozen;
    }

    static ItemCore Core() => new(1000, ItemKind.Consumable, 1)
    {
        Attr = 7, EnchantCardId = 12, AmplifyType = 3, AmplifyValue = 19, Marker16 = 20,
        Chronicle0 = new(21, 1, 2, 3, 4), Chronicle1 = new(22, 2, 3, 4, 5), RemainUseCount = 5, SortLockFlag = 1,
        Random0 = new(1, 2, 3), Random1 = new(2, 3, 4), Random2 = new(3, 4, 5), GuardianGemKeys = 0x1122334455667788,
        Extension114 = new(6, 70), Extension121 = 80
    };

    static void Load(SinglePlayerDungeonRun run)
    {
        if (run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid()).Outcome != DungeonRunOutcome.Accepted)
            throw new InvalidOperationException("FAIL 地面合成房间加载失败");
    }

    static GroundFixture Fixture(bool withMiddle = false)
    {
        var authority = new FixtureAuthority();
        var a = new DungeonMap("map", new(0, 0), 1, "map/fixture-ground-a.map");
        var b = new DungeonMap(withMiddle ? "map" : "boss", new(1, 0), 2, "map/fixture-ground-b.map");
        var c = new DungeonMap("boss", new(2, 0), 3, "map/fixture-ground-c.map");
        DungeonMap[] maps = withMiddle ? [a, b, c] : [a, b];
        var maze = new DungeonMaze(0, maps.Length, 1, "明确合成", [a.Position], [maps[^1].Position], maps, []);
        var entry = new DungeonEntryPlan(99, 17, 3, 3145, 0, 0, maze, a, maps[^1]);
        DungeonMonsterPlan[] plans = withMiddle ? [Plan(a, 100, 2), Plan(b, 102, 1), Plan(c, 103, 1)] : [Plan(a, 100, 2), Plan(b, 102, 1)];
        DungeonRouteConnection[] connections = withMiddle ? [new(a.Position, b.Position), new(b.Position, c.Position)] : [new(a.Position, b.Position)];
        if (!SinglePlayerDungeonRun.TryCreate(entry, 1, 10, new(plans, connections), authority, out var run, out _))
            throw new InvalidOperationException("FAIL 合成地面实例创建失败");
        var reservations = plans.SelectMany(plan => plan.Monsters.Select(monster =>
            new DungeonGroundReservation(monster.ObjectKey, (uint)(400 + monster.ObjectKey - 100), plan.Room.Map, 500, 200, -17))).ToArray();
        return new(run!, new(run!, plans, reservations), authority, plans, reservations);
    }

    static DungeonMonsterPlan Plan(DungeonMap map, ushort key, int count)
    {
        var sources = Enumerable.Range(0, count).Select(_ => new DungeonRoomMonster(1,
            Array.AsReadOnly(new[] { 1, 0, 3, 100, 100, 0, 1, 1 }), "[fixed]", "[normal]", "monster/fixture-ground.mob")).ToArray();
        return DungeonMonsterPlan.FromRoom(new(map, sources, [], []), 99, key);
    }

    sealed record GroundFixture(SinglePlayerDungeonRun Run, DungeonGroundState Ground, FixtureAuthority Authority,
        DungeonMonsterPlan[] Plans, DungeonGroundReservation[] Reservations);

    // 仅用于最小状态机fixture，绝不接入生产或把合成事实当作正常游戏死亡证据。
    sealed class FixtureAuthority : DungeonCombatAuthority
    {
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity, ushort key, long sequence)
            => ConfirmDeath(identity, Guid.NewGuid(), key, sequence);
    }
}
