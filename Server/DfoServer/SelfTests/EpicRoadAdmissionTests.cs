using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EpicRoadAdmissionTests
{
    public static int RunCore()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-epic-road-admission-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "game.db");
        try
        {
            var repository = new CharacterRepository(path);
            var auth = new AuthenticationService(new AuthenticationRepository(path));
            auth.Create("epic-entry-a", "fixture-password");
            auth.Create("epic-entry-b", "fixture-password");
            var owners = new AccountOwners();
            using var firstOwner = owners.Acquire("epic-entry-a", () => { });
            using var secondOwner = owners.Acquire("epic-entry-b", () => { });
            Character Create(AccountOwners.Lease owner, string name) => repository.Create(owner,
                new NewCharacter(name, new(38, 1, 450, 234), null)
                {
                    DungeonState = CharacterDungeonState.NewOrdinary,
                    Inventory = InventorySnapshot.Empty with { Gold = 100 }
                });
            var first = Create(firstOwner, "EpicEntryA");
            var second = Create(secondOwner, "EpicEntryB");
            var clock = new ConsumableUseSessionTests.FakeClock();
            var store = new ServerEventRepository(path);
            var events = new ServerEventService(store, clock);
            InventoryEventPrecondition Guard() => new(events.Read(ServerEventService.EpicRoadId).Schedule, clock);
            DungeonEntryCharge Charge(Character character, uint dungeon, InventoryEventPrecondition? guard,
                bool payment = false, Guid? instance = null) => new(
                    payment ? new InventoryPlan(character.Inventory, character.Inventory with { Gold = 90 }, "epic_road_entry_test") : null,
                    payment ? (ushort)8 : (ushort)0,
                    new(character.Id, 0, character.DungeonState!.LastAttempt + 1, instance ?? Guid.NewGuid(), 1), 400000457)
                { DungeonId = checked((int)dungeon), RequiredEvent = guard };
            void Same(AccountOwners.Lease owner, Character expected, string message)
            {
                var actual = repository.LoadCharacter(owner, expected.Slot);
                That(actual.Revision == expected.Revision && actual.DungeonState == expected.DungeonState &&
                    actual.Fatigue == expected.Fatigue && actual.Inventory.Gold == expected.Inventory.Gold, message);
            }

            // 两图关闭时，连带费用的直接存储调用也不能扣费或预留尝试号。
            events.Override(ServerEventService.EpicRoadId, false);
            foreach (uint dungeon in new uint[] { 400000097, 400000098 })
            {
                Reject<DungeonEventAdmissionRejectedException>(() => repository.CommitDungeonEntry(firstOwner, first, 100,
                    Charge(first, dungeon, Guard(), payment: true)), "10005明确关闭时事务拒绝星河裂缝新入场");
                Same(firstOwner, first, "活动关闭不扣金币、疲劳或副本尝试号");
                var session = new GameSession(repository, null!, firstOwner);
                That(session.EnterFormalDungeon(dungeon, 0, 0, null!, null!) is null &&
                    session.LastFormalDungeonRejection == "epic-road-event-closed",
                    "活动服务缺失时两目标副本在资源规划前拒绝");
            }
            var ordinarySession = new GameSession(repository, null!, firstOwner);
            That(ordinarySession.EnterFormalDungeon(3, 0, 0, null!, null!) is null &&
                ordinarySession.LastFormalDungeonRejection == "session-not-ready-or-no-dungeon-state",
                "活动服务缺失不改变非活动副本原状态拒绝");

            events.Override(ServerEventService.EpicRoadId, true);
            var active = Guard();
            var enabledSession = new GameSession(repository, null!, firstOwner, timeProvider: clock, serverEvents: events);
            That(enabledSession.EnterFormalDungeon(400000097, 0, 0, null!, null!) is null &&
                enabledSession.LastFormalDungeonRejection == "session-not-ready-or-no-dungeon-state",
                "明确传入活动服务且开启时继续原会话资格校验，不绕过Ready或资源规则");
            That(!Charge(first, 400000097, active).IsEmpty,
                "只有活动前置而无票无疲劳的入场仍必须进入受检事务");
            Reject<DungeonEventAdmissionRejectedException>(() => repository.CommitDungeonEntry(firstOwner, first, 100,
                Charge(first, 400000097, null, payment: true)), "两目标副本缺少活动前置时不能绕过服务端准入");
            var stale = Charge(first, 400000097, active, payment: true);
            events.Override(ServerEventService.EpicRoadId, false);
            That(enabledSession.EnterFormalDungeon(400000097, 0, 0, null!, null!) is null &&
                enabledSession.LastFormalDungeonRejection == "epic-road-event-closed",
                "同一会话按原活动服务即时关闭，不维护第二份开关快照");
            Reject<DungeonEventAdmissionRejectedException>(() => repository.CommitDungeonEntry(firstOwner, first, 100, stale),
                "准备完成后关闭活动使本次费用和尝试号整笔拒绝");
            Same(firstOwner, first, "关闭后旧计划不推进角色资产或实例尝试");
            events.Override(ServerEventService.EpicRoadId, true);
            Reject<DungeonEventAdmissionRejectedException>(() => repository.CommitDungeonEntry(firstOwner, first, 100, stale),
                "关闭再开启仍不能复用旧活动版本的入场计划");

            var now = clock.GetUtcNow();
            events.Schedule(ServerEventService.EpicRoadId, now, now.AddSeconds(1));
            var expiring = Charge(first, 400000097, Guard(), payment: true);
            clock.Advance(TimeSpan.FromSeconds(1));
            Reject<DungeonEventAdmissionRejectedException>(() => repository.CommitDungeonEntry(firstOwner, first, 100, expiring),
                "准备后到达结束端点仍由同一事务拒绝，不能只依赖规划时开关");
            Same(firstOwner, first, "时间到期拒绝不留下费用或尝试号");

            events.Override(ServerEventService.EpicRoadId, true);
            active = Guard();
            var instance = Guid.NewGuid();
            var party = new[]
            {
                new PartyDungeonReservation(firstOwner, first, 100, Charge(first, 400000097, active, true, instance), null),
                new PartyDungeonReservation(secondOwner, second, 100, Charge(second, 400000097, stale.RequiredEvent, true, instance), null)
            };
            Reject<DungeonEventAdmissionRejectedException>(() => repository.CommitPartyDungeonEntry(party),
                "后续成员活动版本失效时整队入场原子回滚");
            Same(firstOwner, first, "后续成员拒绝不能留下首成员扣费、疲劳或尝试号");
            Same(secondOwner, second, "失效成员也保持原资产和副本状态");
            party[1] = party[1] with { Charge = Charge(second, 400000097, active, true, instance) };
            var entered = repository.CommitPartyDungeonEntry(party);
            first = entered[0].Character;
            second = entered[1].Character;
            That(first.Inventory.Gold == 90 && second.Inventory.Gold == 90 && first.Fatigue!.Used == 8 &&
                second.Fatigue!.Used == 8 && first.DungeonState!.LastAttempt == 1 && second.DungeonState!.LastAttempt == 1,
                "开启时整队沿原事务只扣一次费用、疲劳并各预留一次尝试");
            Reject<InvalidOperationException>(() => repository.CommitPartyDungeonEntry(party), "旧角色快照不能重复扣取活动入场费用");

            var free = Charge(first, 400000098, Guard());
            first = repository.CommitDungeonEntry(firstOwner, first, 200, free).Character;
            That(first.Inventory.Gold == 90 && first.Fatigue!.Used == 8 && first.DungeonState!.LastAttempt == 2,
                "开启时无费用活动入场仍检查活动后仅预留新尝试号");
            events.Override(ServerEventService.EpicRoadId, false);
            first = repository.Save(firstOwner, first, first.Position, first.Return);
            That(first.DungeonState!.LastAttempt == 2 && first.Inventory.Gold == 90,
                "关闭只约束新增入场，不让已有角色的后续普通保存重新检查活动");
            var ordinary = Charge(second, 3, null, payment: true);
            second = repository.CommitDungeonEntry(secondOwner, second, 200, ordinary).Character;
            That(second.DungeonState!.LastAttempt == 2, "10005关闭不影响其它副本入场事务");

            events.Override(ServerEventService.Strongway2019Id, true);
            var strongwayGuard = new InventoryEventPrecondition(events.Read(ServerEventService.Strongway2019Id).Schedule, clock);
            first = repository.CommitDungeonEntry(firstOwner, first, 300, Charge(first, 5359, strongwayGuard)).Character;
            That(!events.Read(ServerEventService.EpicRoadId).Active && first.DungeonState!.LastAttempt == 3,
                "10005关闭不误拒312有效前置的Strongway原入场事务");
            events.Override(ServerEventService.Strongway2019Id, false);
            Reject<InventoryEventRejectedException>(() => repository.CommitDungeonEntry(firstOwner, first, 400,
                Charge(first, 5359, strongwayGuard)), "312旧活动前置保留原InventoryEventRejectedException，不改作星河拒绝");
            Same(firstOwner, first, "312晚期拒绝保留原资产、revision与尝试号");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) TestFiles.DeleteDirectory(root, true);
        }
        Console.WriteLine($"PASS {Count - before} 星河裂缝活动准入、版本复验与整队事务断言");
        return Count - before;
    }
}
