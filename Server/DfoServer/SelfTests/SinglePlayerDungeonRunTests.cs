using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using PvfLib;

namespace DfoServer;

internal static class SinglePlayerDungeonRunTests
{
    public static void Run(PvfArchive pvf)
    {
        int checks = 0;
        void Check(bool value, string name)
        {
            checks++;
            if (!value)
                throw new InvalidOperationException("FAIL single-player run: " + name);
        }
        var authority = new FixtureCombatAuthority();
        var a = new DungeonMap("map", new(0, 0), 1, "map/fixture-a.map");
        var b = new DungeonMap("map", new(1, 0), 2, "map/fixture-b.map");
        var c = new DungeonMap("boss", new(1, 1), 3, "map/fixture-c.map");
        var d = new DungeonMap("map", new(0, 1), 4, "map/fixture-d.map");
        var maps = Array.AsReadOnly(new[] { a, b, c, d });
        var maze = new DungeonMaze(0, 2, 2, "fixture-only", [a.Position], [c.Position], maps, []);
        var entry = new DungeonEntryPlan(99, 17, 3, 3145, 0, 0, maze, a, c);
        var route = new DungeonFixedRoute([Plan(a, 10, 11), Plan(b, 12), Plan(c, 13), Plan(d, 14)],
            [new(a.Position, b.Position), new(b.Position, c.Position), new(a.Position, d.Position)]);
        Check(SinglePlayerDungeonRun.TryCreate(entry, 1, 23, route, authority, out var created, out _), "explicit fixture creates identity-bound Loading run");
        var run = created!;
        DungeonRunIdentity first = run.Snapshot.Identity;
        Check(run.Phase == SinglePlayerDungeonPhase.Loading && first.CharacterId == 99 && first.QuestId == 3145 && first.TaskAttempt == 1 &&
            first.InstanceId != Guid.Empty && first.Epoch == 23 && run.StartCharacterRevision == 17, "instance identity and opening revision snapshot are separate");
        foreach (DungeonRunIdentity wrong in new[]
        {
            first with { CharacterId = 98 }, first with { QuestId = 3146 }, first with { TaskAttempt = 2 },
            first with { InstanceId = Guid.NewGuid() }, first with { Epoch = 22 }
        })
            Check(run.CompleteLoading(wrong, Guid.NewGuid()).Outcome == DungeonRunOutcome.Rejected, "wrong character quest attempt instance or epoch rejected");
        Check(run.AcceptConfirmedDeath(authority.Death(first, 10, 1)).Outcome == DungeonRunOutcome.Rejected &&
            run.Move(first, Guid.NewGuid(), b.Position).Outcome == DungeonRunOutcome.Rejected, "Loading accepts neither confirmed death nor room transition");
        Guid loaded = Guid.NewGuid();
        Check(run.CompleteLoading(first, loaded).Outcome == DungeonRunOutcome.Accepted && run.Phase == SinglePlayerDungeonPhase.Dungeon &&
            run.Snapshot.Identity.Epoch == 24, "load completion advances exact epoch");
        Check(run.CompleteLoading(first, loaded).Outcome == DungeonRunOutcome.Duplicate && run.Snapshot.Identity.Epoch == 24,
            "accepted load replay does not advance again");
        Check(run.CompleteLoading(first, Guid.NewGuid()).Outcome == DungeonRunOutcome.Rejected, "unknown stale load action is rejected");
        DungeonRunIdentity inA = run.Snapshot.Identity;
        Check(run.Move(inA, Guid.NewGuid(), b.Position).Outcome == DungeonRunOutcome.Rejected, "uncleared room blocks movement");
        Check(run.AcceptConfirmedDeath(new FixtureCombatAuthority().Death(inA, 10, 1)).Outcome == DungeonRunOutcome.Rejected,
            "unbound combat issuer cannot assert death");
        Check(run.AcceptConfirmedDeath(authority.Death(inA, 999, 1)).Outcome == DungeonRunOutcome.Rejected, "unknown object identity rejected");
        var death10 = authority.Death(inA, 10, 1);
        Check(run.AcceptConfirmedDeath(death10).Outcome == DungeonRunOutcome.Accepted && run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 11 }),
            "formal typed death removes only its declared monster");
        Check(run.AcceptConfirmedDeath(death10).Outcome == DungeonRunOutcome.Duplicate && run.Snapshot.LivingMonsters.Count == 1,
            "duplicate confirmed event has no repeated effect");
        Check(run.AcceptConfirmedDeath(authority.Death(inA, 11, 2, death10.EventId)).Outcome == DungeonRunOutcome.Rejected,
            "same event identity with altered payload is rejected");
        Check(run.AcceptConfirmedDeath(authority.Death(inA, 11, 1)).Outcome == DungeonRunOutcome.Rejected, "combat event sequence cannot be reused");
        Check(run.AcceptConfirmedDeath(authority.Death(inA, 10, 2)).Outcome == DungeonRunOutcome.Rejected,
            "another event cannot kill already dead object again");
        Check(run.AcceptConfirmedDeath(authority.Death(inA, 11, 2)).Outcome == DungeonRunOutcome.Accepted && run.Phase == SinglePlayerDungeonPhase.Dungeon,
            "first room clear is not whole-instance settlement");
        Check(run.Move(inA, Guid.NewGuid(), new(9, 9)).Outcome == DungeonRunOutcome.Rejected, "missing target room and jump cannot pass");
        Guid moveD = Guid.NewGuid();
        Check(run.Move(inA, moveD, d.Position).Outcome == DungeonRunOutcome.Accepted && run.Phase == SinglePlayerDungeonPhase.Loading,
            "explicit cleared-room edge enters next loading phase");
        Check(run.Move(inA, moveD, d.Position).Outcome == DungeonRunOutcome.Duplicate, "accepted transition replay does not repeat loading");
        Check(run.Move(inA, moveD, b.Position).Outcome == DungeonRunOutcome.Rejected, "replayed action cannot change its target");
        Check(run.AcceptConfirmedDeath(death10).Outcome == DungeonRunOutcome.Duplicate, "old accepted death replay is harmless in another room");
        Check(run.AcceptConfirmedDeath(authority.Death(inA, 14, 3)).Outcome == DungeonRunOutcome.Rejected, "old room epoch cannot kill new-room monster");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 14, 3));
        Check(run.Move(run.Snapshot.Identity, Guid.NewGuid(), c.Position).Outcome == DungeonRunOutcome.Rejected,
            "adjacent map across absent approved connection is a wall not a passage");
        Check(run.Move(run.Snapshot.Identity, Guid.NewGuid(), a.Position).Outcome == DungeonRunOutcome.Accepted, "cleared side room can revisit approved edge");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        Check(run.Snapshot.LivingMonsters.Count == 0, "revisited room retains confirmed deaths and does not respawn");
        run.Move(run.Snapshot.Identity, Guid.NewGuid(), b.Position);
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        Check(run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 12, 4)).Outcome == DungeonRunOutcome.Accepted,
            "new-room confirmed event accepted under new identity epoch");
        run.Move(run.Snapshot.Identity, Guid.NewGuid(), c.Position);
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        var bossDeath = authority.Death(run.Snapshot.Identity, 13, 5);
        DungeonRunResult settled = run.AcceptConfirmedDeath(bossDeath);
        Check(settled.Outcome == DungeonRunOutcome.Accepted && run.Phase == SinglePlayerDungeonPhase.Settlement &&
            settled.ClearFact!.ConfirmedDeathEvents.Count == 5 && settled.ClearFact!.Identity == run.Snapshot.Identity,
            "all route monsters confirmed dead produces single settlement fact");
        Check(run.AcceptConfirmedDeath(bossDeath).Outcome == DungeonRunOutcome.Duplicate && run.AcceptConfirmedDeath(bossDeath).ClearFact is null,
            "settlement replay cannot reproduce clear fact or trigger reward");
        Check(run.Move(run.Snapshot.Identity, Guid.NewGuid(), b.Position).Outcome == DungeonRunOutcome.Rejected, "settlement cannot continue room transitions");
        DungeonRunIdentity terminal = run.Snapshot.Identity;
        Guid exit = Guid.NewGuid();
        Check(run.Return(terminal, exit).Outcome == DungeonRunOutcome.Accepted && run.Phase == SinglePlayerDungeonPhase.Returned,
            "exact settlement exit terminates instance and advances epoch");
        Check(run.Return(terminal, exit).Outcome == DungeonRunOutcome.Duplicate && run.Return(terminal, Guid.NewGuid()).Outcome == DungeonRunOutcome.Rejected,
            "exit replay is idempotent but another old exit is rejected");
        Check(SinglePlayerDungeonRun.TryCreate(entry, 2, 100, route, authority, out var retry, out _) && retry!.Snapshot.Identity.InstanceId != first.InstanceId,
            "new challenge receives independent attempt instance and epoch");
        retry!.CompleteLoading(retry!.Snapshot.Identity, Guid.NewGuid());
        Check(retry!.AcceptConfirmedDeath(death10).Outcome == DungeonRunOutcome.Rejected && retry!.Snapshot.LivingMonsters.Count == 2,
            "prior challenge confirmed event cannot affect newly spawned objects with same keys");
        Check(SinglePlayerDungeonRun.TryCreate(entry, 3, 200, route, authority, out var cancelled, out _), "another attempt can start for cancellation");
        var cancelIdentity = cancelled!.Snapshot.Identity;
        Check(cancelled!.Return(cancelIdentity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted &&
            cancelled!.CompleteLoading(cancelIdentity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Rejected,
            "cancel during Loading permanently rejects delayed completion");
        Check(!SinglePlayerDungeonRun.TryCreate(entry, 1, 23, null, authority, out _, out _) &&
            !SinglePlayerDungeonRun.TryCreate(entry, 1, 23, route, null, out _, out _), "missing formal route or combat owner is Unsupported");
        Check(!SinglePlayerDungeonRun.TryCreate(entry, 1, 23, route with { Connections = [new(a.Position, c.Position)] }, authority, out _, out _),
            "diagonal connection is rejected before instance publication");
        Check(!SinglePlayerDungeonRun.TryCreate(entry, 1, 23, route with { Connections = [new(a.Position, b.Position)] }, authority, out _, out _),
            "disconnected route cannot become implicitly reachable");
        var unsupportedRoom = route.Rooms[0].Room with { Monsters = [route.Rooms[0].Room.Monsters[0] with { RankKind = "[unknown]" }] };
        Check(!SinglePlayerDungeonRun.TryCreate(entry, 1, 23, route with { Rooms = [new(unsupportedRoom, []), .. route.Rooms.Skip(1)] }, authority, out _, out _),
            "unknown monster mechanism is not replaced by an empty cleared room");
        Check(!SinglePlayerDungeonRun.TryCreate(entry, 0, 23, route, authority, out _, out _) &&
            !SinglePlayerDungeonRun.TryCreate(entry, 1, long.MaxValue, route, authority, out _, out _),
            "invalid attempt and exhausted epoch never publish an unusable instance");
        Check(!SinglePlayerDungeonRun.TryCreate(entry, 1, 23, route with { Rooms = [route.Rooms[0], Plan(b, 10), .. route.Rooms.Skip(2)] }, authority, out _, out _),
            "all rooms share one unique object key namespace");
        Check(!SinglePlayerDungeonRun.TryCreate(entry, 1, 23, route with { Rooms = [Plan(a, 99, 11), .. route.Rooms.Skip(1)] }, authority, out _, out _),
            "monster key cannot alias owning character identity");
        Check(retry!.AcceptConfirmedDeath(authority.Death(retry!.Snapshot.Identity, 10, 1, Guid.Empty)).Outcome == DungeonRunOutcome.Rejected,
            "empty formal event identity cannot become an accepted fact");
        Check(retry!.Snapshot.LivingMonsters is IList<ushort> readonlyKeys && readonlyKeys.IsReadOnly,
            "published room roster cannot mutate instance ownership");
        var actualMaze = DungeonCatalog.Load(pvf).Get(3).Mazes.Single(candidate => candidate.QuestConnectionTokens.Count == 3 && candidate.QuestConnectionTokens[1] == new PvfToken(0, 3145));
        var actualMaps = actualMaze.MapSpecification!;
        var actualEntry = new DungeonEntryPlan(99, 17, 3, 3145, 0, 0, actualMaze,
            actualMaps.Single(map => map.Position == actualMaze.Starts.Single()), actualMaps.Single(map => map.Position == actualMaze.Bosses.Single()));
        Check(!SinglePlayerDungeonRun.TryCreate(actualEntry, 1, 23, null, authority, out _, out _),
            "actual PVF maze availability does not prove wall door or formal entry support");
        // 只验证真实地图字段能接入领域底座：怪物与战斗来源仍明确为合成fixture。
        var actualTopology = actualMaze.StaticTopology;
        var mapContractRoute = new DungeonFixedRoute(
            actualMaps.Select((map, index) => Plan(map, checked((ushort)(100 + index)))).ToArray(),
            actualTopology.Links!.Select(link => new DungeonRouteConnection(link.First, link.Second)).ToArray());
        Check(SinglePlayerDungeonRun.TryCreate(actualEntry, 1, 23, mapContractRoute, authority, out var actualRun, out _) &&
            actualRun!.Snapshot.CurrentMap == actualEntry.StartMap, "真实目录map/boss种类与显式地图身份可接入合成状态机测试");
        var wrongMap = a with { Kind = "normal" };
        var wrongMaze = maze with { MapSpecification = [wrongMap, b, c, d] };
        Check(!SinglePlayerDungeonRun.TryCreate(entry with { Maze = wrongMaze, StartMap = wrongMap }, 1, 23,
            route with { Rooms = [Plan(wrongMap, 10, 11), .. route.Rooms.Skip(1)] }, authority, out _, out _),
            "未知normal地图种类不能因fixture曾使用而进入实例");
        Console.WriteLine($"PASS {checks} single-player lifecycle identity and confirmed-event assertions");
    }

    private static DungeonMonsterPlan Plan(DungeonMap map, params ushort[] keys)
    {
        var sources = keys.Select(_ => new DungeonRoomMonster(1, Array.AsReadOnly(new[] { 1, 0, 3, 100, 100, 0, 1, 1 }),
            "[fixed]", "[normal]", "monster/fixture.mob")).ToArray();
        var room = new DungeonRoomDefinition(map, Array.AsReadOnly(sources), [], []);
        return new(room, Array.AsReadOnly(keys.Select((key, index) => new DungeonPlannedMonster(index, key, 1, 3, sources[index])).ToArray()));
    }

    // 仅为状态机单测合成已确认事实，不是实际伤害、HP 或死亡的战斗算法。
    private sealed class FixtureCombatAuthority : DungeonCombatAuthority
    {
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity, ushort key, long sequence, Guid? eventId = null)
            => ConfirmDeath(identity, eventId ?? Guid.NewGuid(), key, sequence);
    }
}
