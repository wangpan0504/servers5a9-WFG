using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class SelectionContentsTcpTests
{
    internal static void ExpectTowers(TcpPeer peer, TcpTestFixture fixture, TcpTestResources resources, string account)
    {
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        var despair = dungeons.Despair;
        var grief = dungeons.GriefTower;
        int level = Convert.ToInt32(fixture.Query("SELECT level FROM account_adventures WHERE account=$p0", account).Single()[0]);
        int despairMax = despair.AccountMaximum + despair.AdventureCounts
            .Where(row => level >= row.MinimumLevel && level <= row.MaximumLevel).Sum(row => row.AdditionalCount);
        int griefMax = grief.DeclaredAccountMaximum + grief.DeclaredAdventureBands
            .Where(row => level >= row.MinimumLevel && level <= row.MaximumLevel).Sum(row => row.AdditionalCount);
        int used = Convert.ToInt32(fixture.Query("SELECT COUNT(DISTINCT character_id) FROM dungeon_special_entry_receipts WHERE account=$p0 AND service_day=$p1 AND kind=3",
            account, ShopPurchaseCount.Today(fixture.UtcNow)).Single()[0]);
        var griefRows = fixture.Query("SELECT used FROM account_grief_tower WHERE account=$p0 AND game_day=$p1", account, QuestCycle.Day(fixture.UtcNow));
        int griefUsed = griefRows.Length == 0 ? 0 : Convert.ToInt32(griefRows[0][0]);
        peer.Expect(0, 1298, [(byte)Math.Max(0, despairMax-used), (byte)despairMax, (byte)Math.Max(0, griefMax-griefUsed), (byte)griefMax]);
    }

    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int checks = Count;
        var clock = new Clock(new(2026, 10, 31, 5, 59, 0, TimeSpan.FromHours(8)));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "selection-contents"), resources: resources, clock: clock);
        await fixture.StartAsync();
        using (var peer = Open(fixture, "selcounts")) { peer.Create("CountFirst"); peer.Create("CountLast"); }
        var ids = fixture.Query("SELECT id FROM characters WHERE account='selcounts' ORDER BY slot");
        ushort first = Convert.ToUInt16(ids[0][0]), last = Convert.ToUInt16(ids[1][0]);
        fixture.Sql("UPDATE characters SET level=95,slot=CASE WHEN id=$p0 THEN 7 ELSE slot END WHERE account='selcounts'", last);
        int day = ShopPurchaseCount.Today(clock.Now), gameDay = QuestCycle.Day(clock.Now);
        fixture.Sql("""
            INSERT INTO dungeon_special_entry_receipts VALUES($p0,'selcounts','00000000000000000000000000000001',1,$p1,3,11008,1,0,1);
            INSERT INTO dungeon_special_entry_receipts VALUES($p0,'selcounts','00000000000000000000000000000002',2,$p1,3,11008,1,0,1);
            INSERT INTO dungeon_despair_progress VALUES($p0,'selcounts',13,$p1,1);
            INSERT INTO account_grief_tower VALUES('selcounts',$p2,1);
            INSERT INTO character_grief_tower VALUES($p0,'selcounts',$p2,1,9,'00000000000000000000000000000003',3);
            """, first, day, gameDay);
        foreach (var (dungeon, daily, weekly) in new[] { (291100320, 5, 3), (400000016, 1, 1), (8523, 3, 2), (8500, 3, 4), (5428, 1, 1) })
        {
            var seededPeriod = DungeonIncountPolicy.Period(clock.Now, dungeon == 8523 ? 1 : 4);
            fixture.Sql("INSERT INTO character_dungeon_incount VALUES($p0,'selcounts',$p1,$p2,$p3,$p4,$p5)",
                first, dungeon, seededPeriod.Day, daily, seededPeriod.Week, weekly);
        }
        string before = fixture.Snapshot("SELECT revision FROM characters WHERE account='selcounts' ORDER BY id");
        using (var peer = Open(fixture, "selcounts"))
        {
            peer.Send(645); peer.Expect(1, 645, [1,0]); peer.Expect(0,1375); peer.Expect(0,1343);
            ExpectTowers(peer, fixture, resources, "selcounts");
            peer.Send(704, [0]); ExpectTowers(peer, fixture, resources, "selcounts");
            var body = peer.Expect(1,704).Body;
            That(body.Length == 44 && body[0] == 1 && body[1] == 0 && body[2] == 0 && U32(body,3) == 1 &&
                U32(body,7) == 12 && U32(body,19) == SelfTestCatalogs.Dungeons(resources.Archive).GriefTower.DeclaredEnterableCount && U32(body,23) == 8,
                "704完整44字节；失败重试不重复占角色基础额度，塔楼层取当前进度减一供原生加一");
            That(body.AsSpan(36,4).SequenceEqual(new byte[] {7,1,0,1}) && body[42]==1 && body[43]==2,
                "原生尾部字段依次为泰波尔斯、普通超时空、眩惑、希望、魔界大战、公会的实际次数，日耗尽仍保留周剩余");
            peer.Send(704,[1]); ExpectTowers(peer, fixture, resources, "selcounts"); body = peer.Expect(1,704).Body;
            That(body[1] == 1 && U32(body,3) == 0 && U32(body,7) == 0 && U32(body,19) == 0 && U32(body,23) == 0,
                "名册索引1查询真实slot7角色，未挑战角色不继承上一行次数或楼层");
            peer.Send(704,[7]); peer.Expect(1,704,[0]);
            peer.Send(295,Build(w => { w.Write(0u); w.Write(1u); })); peer.Expect(1,295,[1]);
            peer.Send(704,[0]); ExpectTowers(peer,fixture,resources,"selcounts"); body=peer.Expect(1,704).Body;
            That(U32(body,3)==0,"重排后的704采用新发布行序，不能复用旧索引缓存");
            peer.Send(704,[1]); ExpectTowers(peer,fixture,resources,"selcounts"); body=peer.Expect(1,704).Body;
            That(U32(body,3)==1,"重排后原挑战角色移动到新索引1");
            before = fixture.Snapshot("SELECT revision FROM characters WHERE account='selcounts' ORDER BY id");
            clock.Now = clock.Now.AddMinutes(2);
            peer.Send(704,[1]); ExpectTowers(peer,fixture,resources,"selcounts"); body=peer.Expect(1,704).Body;
            That(U32(body,3)==0 && U32(body,19)==0 && U32(body,7)==12 && U32(body,23)==8,
                "06:00跨日只读重置基础次数与账号额度，持久楼层保留");
            That(before==fixture.Snapshot("SELECT revision FROM characters WHERE account='selcounts' ORDER BY id"),
                "悬停查询和跨日投影不保存角色或推进revision");
            peer.Select(); peer.Send(704,[0]); peer.Expect(1,704,[0]);
            peer.Send(7); peer.Expect(1,7,[1]); peer.Expect(0,2);
            peer.Send(704,[0]); ExpectTowers(peer,fixture,resources,"selcounts"); peer.Expect(1,704);
            peer.Send(692, Name("CountProbe")); peer.Expect(1,692,[1]);
        }
        using (var peer = Open(fixture,"selother"))
        {
            peer.Send(704,[0]); peer.Expect(1,704,[0]);
            peer.Send(692, Name("CountProbe")); peer.Expect(1,692,[1]);
        }
        using (var peer = Open(fixture,"selcounts")) { peer.Send(704,[]); peer.Closed(); }
        using (var peer = Open(fixture,"selcounts")) { peer.Send(704,[0,0]); peer.Closed(); }
        await fixture.StopAsync();
        var repository = new CharacterRepository(fixture.Database);
        using var lease = new AccountOwners().Acquire("selcounts", () => { });
        var session = new GameSession(repository, resources.Towns, lease, timeProvider: clock);
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        var incounts = DungeonIncountCatalog.Load(resources.Archive);
        var rules = new SelectionContentsRules(dungeons.Despair,dungeons.GriefTower,incounts,EvildomWarCatalog.Load(resources.Archive,dungeons));
        var roster = session.PrepareRoster(); session.MarkRosterPublished(roster);
        var captured = session.CaptureSelectionContents(rules,0)!;
        That(session.CanPublishSelectionContents(captured),"当前名册快照可以发布悬停结果");
        session.MarkRosterPublished(session.PrepareRoster());
        That(!session.CanPublishSelectionContents(captured),"同epoch重发名册也使排队中的旧行结果失效");
        captured = session.CaptureSelectionContents(rules,0)!;
        clock.Now=clock.Now.AddDays(1);
        That(!session.CanPublishSelectionContents(captured),"发送前跨日拒绝排队中的旧额度");
        var state=repository.ReadSelectionContents(lease,null,clock.Now) with {AdventureLevel=35};
        var towers=rules.Towers(state);
        That(towers.DespairMaximum==dungeons.Despair.AccountMaximum+dungeons.Despair.AdventureCounts
            .Where(row=>35>=row.MinimumLevel&&35<=row.MaximumLevel).Sum(row=>row.AdditionalCount),
            "账号塔上限按实际冒险团区间加成");
        var tay=incounts.Find(291100320)!;
        var period=DungeonIncountPolicy.Period(clock.Now,incounts.WeeklyResetDay(tay.CountKey));
        state=state with {Incounts=[new(tay.CountKey,period,new(tay.Limit,1,0,2),period)]};
        var characterView=rules.Character(state,first,clock.Now);
        That(characterView.TayberrsRemaining==Math.Max(0,tay.WeeklyLimit+2-1),
            "悬停周额度使用独立周上限和追加券，日额度耗尽不错误清零周显示");
        var monday = new DateTimeOffset(2026,11,2,6,1,0,TimeSpan.FromHours(8));
        var hopePeriod = DungeonIncountPolicy.Period(monday.AddMinutes(-2),1);
        state=state with {Incounts=[new(8523,hopePeriod,new(3,3),hopePeriod)]};
        That(rules.Character(state,first,monday).HopeRemaining==3,
            "希望按资源周一06点重置，不能沿用通用周四周期");
        var lukeCycle=new DfoServer.Game.Raids.RaidWeeklyCycle(DayOfWeek.Saturday,new TimeOnly(6,0),TimeSpan.FromHours(8));
        var raidRules=new SelectionContentsRules(dungeons.Despair,dungeons.GriefTower,incounts,null,lukeCycle);
        state=state with {RaidClears=[new(1,lukeCycle.Key(clock.Now),1),new(1,lukeCycle.Key(clock.Now.AddDays(-7)),2)]};
        That(raidRules.Character(state,first,clock.Now).RaidUsed[1]==1,
            "卢克按实际装配策略的周周期读取完成账本，排除历史周");
        captured=session.CaptureSelectionContents(rules,0)!;
        session.ChangeCharacterSlots(0,1);
        That(!session.CanPublishSelectionContents(captured),"重排推进epoch后不能发布旧悬停结果");
        Console.WriteLine($"PASS {Count-checks} selection contents TCP assertions");
    }

    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
