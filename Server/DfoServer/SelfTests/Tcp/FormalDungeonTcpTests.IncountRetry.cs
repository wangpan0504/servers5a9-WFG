using System.Buffers.Binary;
using DfoServer;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Management;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    const int TayberrsDungeon = 291100320, TayberrsSecond = 291100322, TayberrsGuide = 5349, TayberrsStone = 10158964;

    // #1020：泰波尔斯 F10 重开成功扣次后必须下发 NOTI 550（日/周剩余）与 719（每周面板已用），用尽后拒绝并补发 0 剩余。
    public static async Task RunIncountRetryAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        var incounts = DungeonIncountCatalog.Load(resources.Archive);
        var definition = dungeons.Get(TayberrsDungeon);
        var row = incounts.Find(TayberrsDungeon)!;
        That(row.Limit == 5 && row.WeeklyLimit == 10 && row.Dungeons.SequenceEqual(new[] { TayberrsDungeon, TayberrsSecond, TayberrsGuide }),
            "泰波尔斯与 291100322/5349 同行日 5 周 10");
        byte difficulty = (byte)Enumerable.Range(0, 5).First(value => definition.AllowsDifficulty((byte)value, false));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "dungeon-incount-retry"), gm: true, resources: resources);
        await fixture.StartAsync();
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "infinite-fatigue off", out _),
            "限次重开回归关闭无限疲劳");
        RunTayberrsRetry(fixture, resources, definition, incounts, difficulty, exhaust: false);
        RunTayberrsRetry(fixture, resources, definition, incounts, difficulty, exhaust: true);
        await fixture.StopAsync();
        Console.WriteLine($"PASS {Count - before} tayberrs F10 remaining-count TCP assertions");
    }

    static void RunTayberrsRetry(TcpTestFixture fixture, TcpTestResources resources, DungeonDefinition definition,
        DungeonIncountCatalog incounts, byte difficulty, bool exhaust)
    {
        var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == TayberrsDungeon));
        var done = new HashSet<int>(area.Quests) { 6116 };
        if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests[0]);
        var options = resources.Towns.DungeonMaps[area.DungeonWorldMap!.Value].Options.Where(option => option.DungeonId == TayberrsDungeon).ToArray();
        if (!options.Any(option => option.QuestId == -1)) done.Add(options.First(option => !option.InProgress).QuestId);
        done.UnionWith(area.Map(done).DungeonGates.First(gate => gate.Conditions.Length == 0).Quests);
        var walk = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, (ushort)(walk.X + walk.Width / 2), (ushort)(walk.Y + walk.Height / 2));
        string account = exhaust ? "incount1020exh" : "incount1020ok";
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        Character created;
        using (var owner = new AccountOwners().Acquire(account, () => { }))
        {
            var candidate = CharacterCreation.Prepare(exhaust ? "RetryExh" : "RetryOk",
                resources.Characters.Get(0), position, position, resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                { Experience = growth.GetLevelStartExperience(95) }, resources.Learning);
            var repository = new CharacterRepository(fixture.Database);
            created = repository.Create(owner, candidate with
            {
                Level = 95, AcceptedStoryLevel = 95, CompletedQuests = done.ToArray(), InitialLearning = learning
            });
            var inventory = new InventoryService(resources.ItemResources.Items, resources.ItemResources.GoldLimits,
                resources.ItemResources.EpicPieces);
            created = InventoryService.Commit(repository, owner, created, inventory.Grant(created,
                [new(TayberrsStone, exhaust ? 1 : 3)]));
        }
        // 疲劳通知保持已用+剩余=上限，夹具改余额时同步回填已用量。
        fixture.Sql("UPDATE character_fatigue SET used=maximum-50,remaining=50 WHERE character_id=$p0", created.Id);
        if (exhaust)
        {
            var period = DungeonIncountPolicy.Period(DateTimeOffset.UtcNow, incounts.WeeklyResetDay(TayberrsDungeon));
            fixture.Sql("INSERT INTO character_dungeon_incount(character_id,account,dungeon,day,daily_used,week,weekly_used) VALUES($p0,$p1,$p2,$p3,4,$p4,9)",
                created.Id, account, TayberrsDungeon, period.Day, period.Week);
        }
        using var peer = Open(fixture, account);
        peer.Select(created.Slot, prepared: true, resumeTown: true, room: false);
        SelectMap(peer, incompleteHellQuest: HasIncompleteHellQuest(resources, created));
        byte[] request = Selection(difficulty);
        BinaryPrimitives.WriteUInt32LittleEndian(request, TayberrsDungeon);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16), 0);
        peer.Send(16, request);
        var info = peer.Expect(0, 28).Body;
        var map = peer.Expect(0, 29).Body;
        That(U32(info, 0) == TayberrsDungeon && info[4] == difficulty, "泰波尔斯 28 带真实图号与指定难度");
        peer.Expect(0, 14);
        byte daily = exhaust ? (byte)0 : (byte)4, weekly = exhaust ? (byte)0 : (byte)9;
        ExpectSharedCounts(peer, daily, weekly, "首次入场 550 与服务端扣次后剩余一致");
        Load(peer, fatigue: false); peer.Expect(0, 36); peer.Barrier();
        ClearTayberrs(fixture, peer, definition, map, info, resources);
        peer.Send(46, DungeonSettlementProtocolTests.Report(peer.Id, 99));
        That(PartyDungeonTcpTests.Drain(peer).Any(packet => packet is { Kind: 0, Opcode: 35 }), "通关返回翻牌");
        peer.Send(69); peer.Expect(1, 69, [1]);
        peer.Send(70); peer.Expect(1, 70);
        peer.Send(71, [0, 0]); peer.Expect(1, 71); PartyDungeonTcpTests.Drain(peer);
        peer.Send(72, [1, 0]);
        var continued = PartyDungeonTcpTests.Drain(peer);
        if (exhaust)
        {
            That(continued.Any(packet => packet is { Kind: 1, Opcode: 72, Body: [1, 1, 2] }) &&
                continued.Any(packet => packet is { Kind: 0, Opcode: 24 }) &&
                continued.All(packet => packet.Opcode is not (28 or 29)),
                "次数用尽 F10 按回城确认，不创建新实例");
            ExpectSharedCounts(continued.Where(packet => packet is { Kind: 0, Opcode: 550 }).Select(packet => packet.Body).ToArray(),
                0, 0, "用尽拒绝后 550 日/周剩余均为 0");
            That(Used(fixture, peer.Id) == (5, 10) && WeeklySlot(fixture, peer.Id) == 1,
                "用尽拒绝不追加日/周次数或每周面板计数");
            That(fixture.Events().Any(row => row.GetProperty("Kind").GetString() == "formal_dungeon_return" &&
                row.GetProperty("Detail").TryGetProperty("Reason", out var reason) && reason.GetString() == "ordinary-retry-rejected" &&
                row.GetProperty("Detail").TryGetProperty("RejectReason", out var rejection) &&
                rejection.GetString() == "dungeon-incount-exhausted"),
                "用尽原因确为 dungeon-incount-exhausted");
            return;
        }
        int ack = Array.FindIndex(continued, packet => packet is { Kind: 1, Opcode: 72, Body: [1, 1, 0] });
        int transition = Array.FindIndex(continued, packet => packet is { Kind: 0, Opcode: 27 });
        int dungeonInfo = Array.FindIndex(continued, packet => packet is { Kind: 0, Opcode: 28 });
        int startMap = Array.FindIndex(continued, packet => packet is { Kind: 0, Opcode: 29 });
        That(ack >= 0 && transition > ack && dungeonInfo > transition && startMap > dungeonInfo &&
            U32(continued[dungeonInfo].Body) == TayberrsDungeon && continued.Count(packet => packet is { Kind: 0, Opcode: 29 }) == 1,
            "F10 成功按 72、27、28、29 重开当前泰波尔斯");
        That(continued.Any(packet => packet is { Kind: 0, Opcode: 14 }), "F10 扣精炼时空石后投影背包差量");
        ExpectSharedCounts(continued.Where(packet => packet is { Kind: 0, Opcode: 550 }).Select(packet => packet.Body).ToArray(),
            3, 8, "F10 成功后 550 日/周剩余等于服务端 3/8");
        That(Used(fixture, peer.Id) == (2, 2) && WeeklySlot(fixture, peer.Id) == 2,
            "两次入场写入 character_dungeon_incount 2/2 与每周面板列 3 的 used=2");
        var weeklyInfo = continued.Single(packet => packet is { Kind: 0, Opcode: 719 }).Body;
        That(weeklyInfo.Length > 8 && weeklyInfo[1] >= 1 && WeeklyPanelUsed(weeklyInfo, 3) == 2,
            "F10 成功后 719 泰波尔斯列已用次数为 2");
        Load(peer); PartyDungeonTcpTests.Drain(peer);
        peer.Send(42); peer.Expect(1, 42, [1]); peer.Expect(0, 3); peer.Expect(0, 24); RefreshedQuests(peer); peer.Barrier();
    }

    static void ClearTayberrs(TcpTestFixture fixture, TcpPeer peer, DungeonDefinition definition, byte[] map, byte[] info,
        TcpTestResources resources)
    {
        int startMap = (int)U32(map, 14);
        var maze = definition.Mazes.First(row => row.HellParty is null && row.QuestConnectionTokens.Count == 0 &&
            row.MapSpecification is { } maps && maps.Any(spec => spec.Id == startMap));
        var goal = new DungeonPoint(info[8], info[9]);
        var route = QuestStoryRouteTests.Path(maze.StaticTopology, new(map[0], map[1]), goal);
        foreach (var point in route)
        {
            bool bossRoom = point == goal;
            bool sawSimpleReward = false;
            for (int index = 0; index < map[18]; index++)
            {
                int offset = 19 + index * 22;
                if (map[offset + 13] > 3 || map[offset + 21] != 0) continue;
                bool isBoss = map[offset + 13] == 3;
                var report = Death(U16(map, offset + 6), peer.Id, map[offset + 12]);
                peer.Send(39, report);
                var drained = PartyDungeonTcpTests.Drain(peer);
                foreach (var notice in drained.Where(packet => packet is { Kind: 0, Opcode: 38 }))
                {
                    ushort dropCount = U16(notice.Body, 2);
                    if (!bossRoom || !isBoss)
                        That(dropCount == 0, "泰波尔斯小怪与绿名怪不产生任何地面掉落");
                    else
                    {
                        for (int dropIndex = 0; dropIndex < dropCount; dropIndex++)
                        {
                            uint dropItemId = U32(notice.Body, 4 + dropIndex * 131 + 6);
                            That(dropItemId is TayberrsDropPolicy.CarbonCrystalId or TayberrsDropPolicy.AbyssInvitationId,
                                "泰波尔斯领主地面只掉落碳结晶体或深渊派对邀请函：" + dropItemId);
                        }
                    }
                    PickupResourceDrops(fixture, peer, notice.Body, 0, resources);
                }
                if (bossRoom && isBoss)
                {
                    var simple = drained.Single(packet => packet is { Kind: 0, Opcode: 1610 }).Body;
                    uint count = U32(simple, 0);
                    That(count is 1 or 2 && simple.Length == 4 + count * 8 &&
                        U32(simple, 4) == 10305821 && U32(simple, 8) == 2 &&
                        (count == 1 || U32(simple, 12) == 10305820 && U32(simple, 16) == 1) &&
                        drained.Any(packet => packet is { Kind: 0, Opcode: 14 }),
                        "泰波尔斯领主死亡同步下发NOTI 14入包差量与NOTI 1610获得物品横幅");
                    sawSimpleReward = true;
                }
            }
            if (point == goal)
            {
                That(sawSimpleReward, "泰波尔斯终点领主死亡必触发NOTI 1610");
                break;
            }
            var next = route[Array.IndexOf(route, point) + 1];
            byte[] move = new byte[100]; move[0] = (byte)next.X; move[1] = (byte)next.Y;
            peer.Send(45, move); peer.Expect(1, 45, [1]); map = peer.Expect(0, 29).Body;
            Load(peer); PartyDungeonTcpTests.Drain(peer);
        }
    }

    static void ExpectSharedCounts(TcpPeer peer, byte daily, byte weekly, string reason)
    {
        ExpectSharedCounts([peer.Expect(0, 550).Body, peer.Expect(0, 550).Body, peer.Expect(0, 550).Body], daily, weekly, reason);
    }

    static void ExpectSharedCounts(byte[][] bodies, byte daily, byte weekly, string reason)
    {
        That(bodies.Length == 3 &&
            bodies.Select(body => U32(body, 0)).OrderBy(id => id).SequenceEqual(new uint[] { TayberrsGuide, TayberrsDungeon, TayberrsSecond }) &&
            bodies.All(body => body.Length == 6 && body[4] == daily && body[5] == weekly),
            reason + "：" + string.Join(',', bodies.Select(body => $"{U32(body, 0)}:{body[4]}/{body[5]}")));
    }

    static (long Daily, long Weekly) Used(TcpTestFixture fixture, int characterId)
    {
        var row = fixture.Query("SELECT daily_used,weekly_used FROM character_dungeon_incount WHERE character_id=$p0 AND dungeon=$p1",
            characterId, TayberrsDungeon);
        return row is [[long daily, long weekly]] ? (daily, weekly) : (0, 0);
    }

    static long WeeklySlot(TcpTestFixture fixture, int characterId)
    {
        var row = fixture.Query("SELECT used FROM character_weekly_dungeon WHERE character_id=$p0 AND slot=3", characterId);
        return row.Length == 0 ? 0 : Convert.ToInt64(row[0][0]);
    }

    static int WeeklyPanelUsed(byte[] body, int slot)
    {
        int nameLength = checked((int)U32(body, 2));
        return checked((int)U32(body, 6 + nameLength + slot * 4));
    }
}
