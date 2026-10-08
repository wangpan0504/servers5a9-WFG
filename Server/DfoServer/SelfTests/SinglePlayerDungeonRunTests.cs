using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using PvfLib;

namespace DfoServer;

internal static class SinglePlayerDungeonRunTests
{
    public static void RunCore()
    {
        int checks = 0;
        void Check(bool value, string name)
        {
            checks++;
            if (!value)
                throw new InvalidOperationException("FAIL nonblocking dungeon actor: " + name);
        }
        var authority = new FixtureCombatAuthority();
        var start = new DungeonMap("map", new(0, 0), 1, "map/fixture-start.map");
        var friendly = new DungeonMap("map", new(1, 0), 2, "map/fixture-friendly.map");
        var boss = new DungeonMap("boss", new(2, 0), 3, "map/fixture-boss.map");
        var maze = new DungeonMaze(0, 3, 1, "fixture-teams", [start.Position], [boss.Position], [start, friendly, boss], []);
        var entry = new DungeonEntryPlan(99, 17, 3, 3145, 0, 0, maze, start, boss);
        var route = new DungeonFixedRoute([WithTeams(Plan(start, 10, 11), 100, 0),
            WithTeams(Plan(friendly, 12), 0), WithTeams(BossPlan(boss, 13, 14), 100, 0)],
            [new(start.Position, friendly.Position), new(friendly.Position, boss.Position)]);
        Check(SinglePlayerDungeonRun.TryCreate(entry, 1, 100, route, authority, out var created, out _),
            "resource-qualified friendly actors preserve the full route and key namespace");
        var run = created!;
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        var original = run.Snapshot;
        Check(original.LivingMonsters.SequenceEqual(new ushort[] { 10, 11 }) && run.CurrentLivingMonster(10) is { IsBlocking: true } &&
            run.CurrentLivingMonster(11) is null, "snapshot retains the friendly actor while combat qualification excludes it");
        Check(run.Move(original.Identity, Guid.NewGuid(), friendly.Position).Outcome == DungeonRunOutcome.Rejected,
            "a living hostile still blocks movement despite its friendly companion");
        var friendlyDeath = authority.Death(original.Identity, 11, 1);
        Check(run.PrepareConfirmedDeath(friendlyDeath) is { Outcome: DungeonRunOutcome.Rejected, Pending: null } &&
            run.AcceptConfirmedDeath(friendlyDeath).Outcome == DungeonRunOutcome.Rejected && run.Snapshot.Identity == original.Identity &&
            run.Snapshot.LivingMonsters.SequenceEqual(original.LivingMonsters) && run.ClearFact is null,
            "friendly reports do not prepare deaths consume sequences change actors or generate clear events");
        var hostileDeath = authority.Death(original.Identity, 10, 1);
        Check(run.AcceptConfirmedDeath(hostileDeath).Outcome == DungeonRunOutcome.Accepted &&
            run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 11 }), "only the hostile death removes an actor");
        Check(run.Move(run.Snapshot.Identity, Guid.NewGuid(), friendly.Position).Outcome == DungeonRunOutcome.Accepted,
            "the surviving friendly companion does not block a cleared room");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        Check(run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 12 }) && run.CurrentLivingMonster(12) is null &&
            run.Move(run.Snapshot.Identity, Guid.NewGuid(), boss.Position).Outcome == DungeonRunOutcome.Accepted,
            "an all-friendly intermediate room remains populated and permits its approved passage");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        var beforeBoss = run.Snapshot;
        var bossDeath = authority.Death(beforeBoss.Identity, 13, 2);
        var pending = run.PrepareConfirmedDeath(bossDeath).Pending!;
        Check(pending.Result is { Snapshot.Phase: SinglePlayerDungeonPhase.Settlement, ClearFact.ConfirmedDeathEvents.Count: 2 } &&
            pending.Result.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 14 }) &&
            pending.Result.ClearFact!.ConfirmedDeathEvents.SequenceEqual(new[] { hostileDeath.EventId, bossDeath.EventId }) &&
            run.Phase == SinglePlayerDungeonPhase.Dungeon && run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 13, 14 }),
            "last hostile prepares settlement with friendly actors alive and no fabricated friendly event");
        var committed = run.ConfirmCommittedDeath(pending);
        Check(committed.ClearFact is not null && ReferenceEquals(run.ClearFact, committed.ClearFact) &&
            run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 14 }) && run.Phase == SinglePlayerDungeonPhase.Settlement,
            "committed hostile death preserves the actual clear fact reference and friendly actor");
        Check(run.AcceptConfirmedDeath(bossDeath).Outcome == DungeonRunOutcome.Duplicate &&
            run.AcceptConfirmedDeath(authority.Death(beforeBoss.Identity, 13, 3)).Outcome == DungeonRunOutcome.Rejected &&
            run.ClearFact!.ConfirmedDeathEvents.Count == 2 && run.ConfirmCommittedDeath(pending).ClearFact is null,
            "same monster repeats cannot create another death receipt or clear reward fact");
        foreach (int index in new[] { 0, 1 })
        {
            var firstRoom = route.Rooms[0];
            var changed = firstRoom.Monsters[index] with { IsBlocking = !firstRoom.Monsters[index].IsBlocking };
            var forged = firstRoom with { Monsters = firstRoom.Monsters.Select((monster, ordinal) => ordinal == index ? changed : monster).ToArray() };
            Check(!SinglePlayerDungeonRun.TryCreate(entry, 2, 200, route with { Rooms = [forged, .. route.Rooms.Skip(1)] }, authority, out _, out _),
                "caller cannot forge blocking eligibility away from the same resource row");
        }
        var defaultRoute = route with { Rooms = [Plan(start, 10, 11), .. route.Rooms.Skip(1)] };
        Check(SinglePlayerDungeonRun.TryCreate(entry, 3, 300, defaultRoute, authority, out var defaultRun, out _),
            "fixtures without team metadata retain their former lifecycle contract");
        defaultRun!.CompleteLoading(defaultRun.Snapshot.Identity, Guid.NewGuid());
        defaultRun.AcceptConfirmedDeath(authority.Death(defaultRun.Snapshot.Identity, 10, 1));
        Check(defaultRun.CurrentLivingMonster(11) is { IsBlocking: true } &&
            defaultRun.Move(defaultRun.Snapshot.Identity, Guid.NewGuid(), friendly.Position).Outcome == DungeonRunOutcome.Rejected,
            "missing team still requires every ordinary monster death");
        var emptyBossRoute = route with { Rooms = [route.Rooms[0], route.Rooms[1], WithTeams(Plan(boss, 13, 14), 0, 0)] };
        Check(SinglePlayerDungeonRun.TryCreate(entry, 4, 400, emptyBossRoute, authority, out var emptyBossRun, out _),
            "all-friendly terminal fixture retains both declared actors");
        emptyBossRun!.CompleteLoading(emptyBossRun.Snapshot.Identity, Guid.NewGuid());
        var onlyDeath = authority.Death(emptyBossRun.Snapshot.Identity, 10, 1);
        emptyBossRun.AcceptConfirmedDeath(onlyDeath);
        emptyBossRun.Move(emptyBossRun.Snapshot.Identity, Guid.NewGuid(), friendly.Position);
        emptyBossRun.CompleteLoading(emptyBossRun.Snapshot.Identity, Guid.NewGuid());
        emptyBossRun.Move(emptyBossRun.Snapshot.Identity, Guid.NewGuid(), boss.Position);
        var loadedBoss = emptyBossRun.CompleteLoading(emptyBossRun.Snapshot.Identity, Guid.NewGuid());
        Check(loadedBoss.ClearFact!.ConfirmedDeathEvents.SequenceEqual(new[] { onlyDeath.EventId }) &&
            loadedBoss.Snapshot.Phase == SinglePlayerDungeonPhase.Settlement &&
            loadedBoss.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 13, 14 }),
            "the original all-blocking-clear path preserves only real death events with friendly actors alive");
        var multiBoss = WithTeams(BossPlan(boss, 20, 21, 22, 23), 100, 100, 100, 0);
        var multiSources = multiBoss.Room.Monsters.Select((source, index) => index == 2 ? source with { RankKind = "[normal]" } : source).ToArray();
        multiBoss = DungeonMonsterPlan.FromRoom(multiBoss.Room with { Monsters = Array.AsReadOnly(multiSources) }, 99, 20);
        var terminalMaze = maze with { Starts = [boss.Position], MapSpecification = [boss] };
        var terminalEntry = entry with { Maze = terminalMaze, StartMap = boss };
        Check(SinglePlayerDungeonRun.TryCreate(terminalEntry, 5, 500, new([multiBoss], []), authority, out var multiRun, out _),
            "multiple blocking bosses normal hostiles and a friendly boss share the original run");
        multiRun!.CompleteLoading(multiRun.Snapshot.Identity, Guid.NewGuid());
        var firstBossDeath = authority.Death(multiRun.Snapshot.Identity, 20, 1);
        var firstBossResult = multiRun.AcceptConfirmedDeath(firstBossDeath);
        Check(firstBossResult.ClearFact is null && multiRun.Phase == SinglePlayerDungeonPhase.Dungeon &&
            multiRun.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 21, 22, 23 }),
            "one dead boss cannot clear while another blocking boss remains alive");
        var finalBossDeath = authority.Death(multiRun.Snapshot.Identity, 21, 2);
        var multiClear = multiRun.AcceptConfirmedDeath(finalBossDeath);
        Check(multiClear.ClearFact!.ConfirmedDeathEvents.SequenceEqual(new[] { firstBossDeath.EventId, finalBossDeath.EventId }) &&
            multiClear.Snapshot.Phase == SinglePlayerDungeonPhase.Settlement &&
            multiClear.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 22, 23 }),
            "all blocking bosses clear despite living normal enemies and friendly bosses without fabricating their deaths");
        var side = new DungeonMap("map", new(0, 1), 4, "map/fixture-side.map");
        var sideMaze = new DungeonMaze(0, 3, 2, "fixture-uncleared-side", [start.Position], [boss.Position], [start, friendly, boss, side], []);
        var sideRoute = route with
        {
            Rooms = [.. route.Rooms, Plan(side, 15)],
            Connections = [.. route.Connections, new(start.Position, side.Position)]
        };
        Check(SinglePlayerDungeonRun.TryCreate(entry with { Maze = sideMaze }, 6, 600, sideRoute, authority, out var sideRun, out _),
            "uncleared branch fixture uses only approved room connections");
        sideRun!.CompleteLoading(sideRun.Snapshot.Identity, Guid.NewGuid());
        sideRun.AcceptConfirmedDeath(authority.Death(sideRun.Snapshot.Identity, 10, 1));
        sideRun.Move(sideRun.Snapshot.Identity, Guid.NewGuid(), friendly.Position);
        sideRun.CompleteLoading(sideRun.Snapshot.Identity, Guid.NewGuid());
        sideRun.Move(sideRun.Snapshot.Identity, Guid.NewGuid(), boss.Position);
        sideRun.CompleteLoading(sideRun.Snapshot.Identity, Guid.NewGuid());
        var prematureBoss = sideRun.AcceptConfirmedDeath(authority.Death(sideRun.Snapshot.Identity, 13, 2));
        Check(prematureBoss.ClearFact is null && sideRun.Phase == SinglePlayerDungeonPhase.Dungeon &&
            sideRun.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 14 }), "an uncleared earlier room prevents terminal boss settlement");
        sideRun.Move(sideRun.Snapshot.Identity, Guid.NewGuid(), friendly.Position);
        sideRun.CompleteLoading(sideRun.Snapshot.Identity, Guid.NewGuid());
        sideRun.Move(sideRun.Snapshot.Identity, Guid.NewGuid(), start.Position);
        sideRun.CompleteLoading(sideRun.Snapshot.Identity, Guid.NewGuid());
        sideRun.Move(sideRun.Snapshot.Identity, Guid.NewGuid(), side.Position);
        sideRun.CompleteLoading(sideRun.Snapshot.Identity, Guid.NewGuid());
        sideRun.AcceptConfirmedDeath(authority.Death(sideRun.Snapshot.Identity, 15, 3));
        sideRun.Move(sideRun.Snapshot.Identity, Guid.NewGuid(), start.Position);
        sideRun.CompleteLoading(sideRun.Snapshot.Identity, Guid.NewGuid());
        sideRun.Move(sideRun.Snapshot.Identity, Guid.NewGuid(), friendly.Position);
        sideRun.CompleteLoading(sideRun.Snapshot.Identity, Guid.NewGuid());
        sideRun.Move(sideRun.Snapshot.Identity, Guid.NewGuid(), boss.Position);
        var delayedClear = sideRun.CompleteLoading(sideRun.Snapshot.Identity, Guid.NewGuid());
        Check(delayedClear.ClearFact is { ConfirmedDeathEvents.Count: 3 } &&
            delayedClear.Snapshot.Phase == SinglePlayerDungeonPhase.Settlement &&
            delayedClear.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 14 }),
            "loading uses the same boss predicate once all prior blocking actors are actually dead");
        Console.WriteLine($"PASS {checks} dungeon nonblocking actor lifecycle assertions");
    }

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
        foreach (var changed in new[]
        {
            route.Rooms[0].Monsters[0] with { CandidateLevel = 1 },
            route.Rooms[0].Monsters[0] with { SpawnType = 3 }
        })
        {
            var changedRoom = route.Rooms[0] with { Monsters = [changed, .. route.Rooms[0].Monsters.Skip(1)] };
            Check(!SinglePlayerDungeonRun.TryCreate(entry, 1, 23, route with { Rooms = [changedRoom, .. route.Rooms.Skip(1)] }, authority, out _, out _),
                "实例不接受与完整资源规划不一致的等级或生成类型");
        }
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
        // 合成已确认事件验证提交协调，不代替生产战斗来源或真实奖励事务。
        var transactionMaze = new DungeonMaze(0, 2, 2, "fixture-commit", [c.Position], [c.Position], [c], []);
        var transactionEntry = entry with { Maze = transactionMaze, StartMap = c, BossMap = c };
        var transactionRoute = new DungeonFixedRoute([Plan(c, 20, 21)], []);
        Check(SinglePlayerDungeonRun.TryCreate(transactionEntry, 4, 300, transactionRoute, authority, out var coordinated, out _),
            "提交协调fixture建立独立实例");
        var committingRun = coordinated!;
        committingRun.CompleteLoading(committingRun.Snapshot.Identity, Guid.NewGuid());
        var originalSnapshot = committingRun.Snapshot;
        var firstDeath = authority.Death(originalSnapshot.Identity, 20, 1);
        var preparation = committingRun.PrepareConfirmedDeath(firstDeath);
        var pending = preparation.Pending!;
        Check(preparation.Outcome == DungeonRunOutcome.Accepted && pending.Result.ClearFact is null &&
            pending.Result.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 21 }) &&
            committingRun.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 20, 21 }) &&
            committingRun.Snapshot.Identity == originalSnapshot.Identity && committingRun.Phase == SinglePlayerDungeonPhase.Dungeon,
            "准备死亡只冻结候选，不消费死亡事件或改变实例");
        Check(ReferenceEquals(committingRun.PrepareConfirmedDeath(firstDeath).Pending, pending) &&
            ReferenceEquals(committingRun.PrepareConfirmedDeath(authority.Death(originalSnapshot.Identity, 20, 1, firstDeath.EventId)).Pending, pending),
            "待提交相同事实重复准备保留同一候选与稳定事件ID");
        Check(committingRun.PrepareConfirmedDeath(authority.Death(originalSnapshot.Identity, 21, 2, firstDeath.EventId)).Outcome == DungeonRunOutcome.Rejected &&
            committingRun.PrepareConfirmedDeath(new FixtureCombatAuthority().Death(originalSnapshot.Identity, 20, 1, firstDeath.EventId)).Outcome == DungeonRunOutcome.Rejected,
            "同ID篡改载荷或更换签发来源不能替换待提交事实");
        Check(committingRun.PrepareConfirmedDeath(authority.Death(originalSnapshot.Identity, 21, 2)).Outcome == DungeonRunOutcome.Rejected &&
            committingRun.Return(originalSnapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Rejected &&
            committingRun.Move(originalSnapshot.Identity, Guid.NewGuid(), b.Position).Outcome == DungeonRunOutcome.Rejected &&
            committingRun.AcceptConfirmedDeath(firstDeath).Outcome == DungeonRunOutcome.Rejected,
            "待提交期间新死亡、退出、过房与旧入口都不能抢先改变状态");
        var forged = new PreparedDungeonDeath(firstDeath, pending.Monster, pending.Result);
        Check(committingRun.ConfirmCommittedDeath(forged).Outcome == DungeonRunOutcome.Rejected &&
            !committingRun.CancelUncommittedDeath(forged), "重新构造的候选不能确认或撤销原准备");
        Check(committingRun.CancelUncommittedDeath(pending) && !committingRun.CancelUncommittedDeath(pending) &&
            committingRun.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 20, 21 }),
            "明确未提交时撤销准备，死亡集和epoch保持原样");
        var retryPending = committingRun.PrepareConfirmedDeath(firstDeath).Pending!;
        Check(!ReferenceEquals(retryPending, pending) && retryPending.Death.EventId == firstDeath.EventId &&
            committingRun.ConfirmCommittedDeath(pending).Outcome == DungeonRunOutcome.Rejected &&
            !committingRun.CancelUncommittedDeath(pending), "回滚后使用原事件重试，旧候选不能消费或释放新准备");
        Check(committingRun.ConfirmCommittedDeath(retryPending).Outcome == DungeonRunOutcome.Accepted &&
            committingRun.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 21 }) &&
            committingRun.ConfirmCommittedDeath(retryPending).Outcome == DungeonRunOutcome.Duplicate &&
            committingRun.ConfirmCommittedDeath(retryPending).ClearFact is null &&
            committingRun.PrepareConfirmedDeath(firstDeath).Outcome == DungeonRunOutcome.Duplicate,
            "提交确认只消费一次；重复确认不重新输出奖励或清图");
        var lastDeath = authority.Death(committingRun.Snapshot.Identity, 21, 2);
        var lastPending = committingRun.PrepareConfirmedDeath(lastDeath).Pending!;
        Check(committingRun.AcceptConfirmedDeath(firstDeath).Outcome == DungeonRunOutcome.Duplicate &&
            committingRun.IsPendingDeath(lastPending), "末次候选待提交时，先前已确认死亡仍按重复处理");
        Check(lastPending.Result.ClearFact is { ConfirmedDeathEvents.Count: 2 } &&
            lastPending.Result.ClearFact.Identity == lastPending.Result.Snapshot.Identity &&
            lastPending.Result.ClearFact.ConfirmedDeathEvents.Contains(firstDeath.EventId) &&
            lastPending.Result.ClearFact.ConfirmedDeathEvents.Contains(lastDeath.EventId) &&
            lastPending.Result.Snapshot.Phase == SinglePlayerDungeonPhase.Settlement &&
            lastPending.Result.Snapshot.Identity.Epoch == originalSnapshot.Identity.Epoch + 1 &&
            committingRun.Phase == SinglePlayerDungeonPhase.Dungeon && committingRun.Snapshot.LivingMonsters.Count == 1,
            "末次死亡在提交前提供冻结清图候选，实际实例尚未结算");
        Check(committingRun.CancelUncommittedDeath(lastPending) && committingRun.Phase == SinglePlayerDungeonPhase.Dungeon &&
            committingRun.Snapshot.Identity == originalSnapshot.Identity,
            "末次死亡回滚不丢失清图资格或提前推进epoch");
        var lastRetry = committingRun.PrepareConfirmedDeath(lastDeath).Pending!;
        Check(lastRetry.Result.ClearFact!.ConfirmedDeathEvents.SequenceEqual(lastPending.Result.ClearFact!.ConfirmedDeathEvents) &&
            lastRetry.Result.Snapshot.Identity == lastPending.Result.Snapshot.Identity,
            "回滚后的同事实重试保持清图身份和全部死亡事件ID");
        Check(SinglePlayerDungeonRun.TryCreate(transactionEntry, 5, 400, transactionRoute, authority, out var otherRun, out _),
            "另一个实例用于候选隔离验证");
        otherRun!.CompleteLoading(otherRun.Snapshot.Identity, Guid.NewGuid());
        Check(otherRun.ConfirmCommittedDeath(lastRetry).Outcome == DungeonRunOutcome.Rejected &&
            !otherRun.CancelUncommittedDeath(lastRetry) && otherRun.Snapshot.LivingMonsters.Count == 2,
            "另一个实例不能消费或撤销原实例待提交候选");
        var committed = committingRun.ConfirmCommittedDeath(lastRetry);
        Check(ReferenceEquals(committed, lastRetry.Result) && committingRun.Phase == SinglePlayerDungeonPhase.Settlement &&
            committingRun.Snapshot.Identity == lastRetry.Result.Snapshot.Identity && committingRun.Snapshot.LivingMonsters.Count == 0 &&
            committingRun.ConfirmCommittedDeath(lastRetry).ClearFact is null && !committingRun.CancelUncommittedDeath(lastRetry),
            "提交后发布准备时的同一清图结果，不能撤销或重复结算");
        Check(pending.Result.Snapshot.LivingMonsters is IList<ushort> frozenLiving && frozenLiving.IsReadOnly &&
            lastRetry.Result.ClearFact!.ConfirmedDeathEvents is IList<Guid> frozenEvents && frozenEvents.IsReadOnly,
            "待提交名单与清图事件集保持只读快照");
        Check(committingRun.Return(committingRun.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted,
            "确认完成后解除待提交门禁，正常退出仍可执行");
        Console.WriteLine($"PASS {checks} single-player lifecycle identity and confirmed-event assertions");
    }

    private static DungeonMonsterPlan Plan(DungeonMap map, params ushort[] keys)
    {
        var sources = keys.Select(_ => new DungeonRoomMonster(1, Array.AsReadOnly(new[] { 1, 0, 3, 100, 100, 0, 1, 1 }),
            "[fixed]", "[normal]", "monster/fixture.mob")).ToArray();
        var room = new DungeonRoomDefinition(map, Array.AsReadOnly(sources), [], []);
        return new(room, Array.AsReadOnly(keys.Select((key, index) => new DungeonPlannedMonster(index, key, 1, 3, sources[index])).ToArray()));
    }

    private static DungeonMonsterPlan WithTeams(DungeonMonsterPlan plan, params int[] teams)
        => DungeonMonsterPlan.FromRoom(plan.Room with { MonsterTeams = Array.AsReadOnly(teams) }, 99, plan.Monsters[0].ObjectKey);

    private static DungeonMonsterPlan BossPlan(DungeonMap map, params ushort[] keys)
    {
        var plan = Plan(map, keys);
        var sources = plan.Room.Monsters.Select(source => source with { RankKind = "[boss]" }).ToArray();
        return DungeonMonsterPlan.FromRoom(plan.Room with { Monsters = Array.AsReadOnly(sources) }, 99, keys[0]);
    }

    // 仅为状态机单测合成已确认事实，不是实际伤害、HP 或死亡的战斗算法。
    private sealed class FixtureCombatAuthority : DungeonCombatAuthority
    {
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity, ushort key, long sequence, Guid? eventId = null)
            => ConfirmDeath(identity, eventId ?? Guid.NewGuid(), key, sequence);
    }
}
