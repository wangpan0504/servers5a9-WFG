using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Marriage;
using DfoServer.Network.Management;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class FatigueDailyResetTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        FatigueBatteryTests.RunResources(resources);
        await FormalDungeonTcpTests.RunFatigueAccelerationExperienceAsync(options, Path.Combine(root, "battery"), resources, false, true);
        ServerItemCycleTests.RunResource(resources.Archive);
        var zone = TimeZoneInfo.Utc;
        var clock = new DailyBoosterTestClock(DateTimeOffset.Parse("2030-05-02T05:59:59Z"), zone);
        var localResetAt = DateTimeOffset.Parse("2030-05-02T06:00:00Z");
        var legacyResetAt = DateTimeOffset.Parse("2030-05-02T22:00:00Z");
        int day = new DateOnly(2030, 5, 1).DayNumber;
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "fatigue-daily"), gm: true, resources: resources, clock: clock);
        var repository = new CharacterRepository(fixture.Database);
        ushort first, second;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var plan = CharacterCreation.Prepare("DailyFirst", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get) with { CompletedQuests = [3145] };
            first = repository.Create(owner, plan).Id;
            repository.Create(owner, plan with { Name = "DailyOffline" });
        }
        using (var owner = new AccountOwners().Acquire("fatigue-other", () => { }))
        {
            var plan = CharacterCreation.Prepare("DailyOther", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get);
            second = repository.Create(owner, plan).Id;
        }
        var supplyOwners = new AccountOwners();
        using (var firstOwner = supplyOwners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        using (var secondOwner = supplyOwners.Acquire("fatigue-other", () => { }))
        {
            var a = repository.LoadCharacter(firstOwner, 0); var b = repository.LoadCharacter(secondOwner, 0);
            var ticket = resources.ItemResources.Items.Get(10002609);
            InventorySnapshot WithTickets(InventorySnapshot inventory, int count) => inventory with
            {
                Items = inventory.Items.Add(new(0, new(InventoryListType.Main, checked((short)ItemSlotBoundService.Range(ticket.Kind, inventory).Start)),
                    new(ticket.Id, ticket.Kind, count))),
                ServerItems = inventory.ServerItems with { RefillDay = day, Refills = [new(ticket.Id, day)] }
            };
            var marriage = MarriageWeddingRules.CreateEngagement(Guid.NewGuid(), 501, a.Id, b.Id, clock.GetUtcNow().ToUnixTimeSeconds()) with
                { State = 4, RingLevel = 0 };
            repository.CommitMarriageTogether(firstOwner, secondOwner, a, b,
                new(a.Inventory, WithTickets(a.Inventory, 2), "marriage_daily_fixture"),
                new(b.Inventory, WithTickets(b.Inventory, 5), "marriage_daily_fixture"), null, marriage);
        }
        fixture.Sql("UPDATE character_fatigue SET used=149,remaining=7,used_maximum=23,context_auxiliary=11,selection_auxiliary=9");
        fixture.Sql("UPDATE character_fatigue_reset_days SET service_day=$p0", day);
        fixture.Sql("UPDATE character_fatigue SET used=155,remaining=1 WHERE character_id=$p0", first);
        int savedQuestDay = DfoServer.Game.Quests.QuestCycle.Day(DateTimeOffset.UtcNow.AddDays(1));
        fixture.Sql("UPDATE characters SET quest_cycle_day=$p0 WHERE id=$p1", savedQuestDay, first);
        await fixture.StartAsync();
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "infinite-fatigue off", out _), "跨日回归关闭无限疲劳，核对真实余额");
        using (var peer = Open(fixture))
        using (var other = Open(fixture, "fatigue-other"))
        {
            peer.Select(); peer.Barrier(); other.Select(); other.Barrier();
            That(Tickets(first) == 2 && Tickets(second) == 5, "本机06:00前不提前补票，也不截断另一账号的超量门票");
            That(fixture.Query("SELECT quest_cycle_day FROM characters WHERE id=$p0", first) is [[long storedDay]] && storedDay == savedQuestDay &&
                fixture.Query("SELECT count(*) FROM character_completed_quests WHERE character_id=$p0 AND quest_id=3145", first) is [[1L]],
                "真实选角允许系统时间早于已存任务服务日，登录不清任务或回退周期");
            FormalDungeonTcpTests.EntryTown(peer);
            byte[] information = [];
            var map = FormalDungeonTcpTests.Enter(peer, information: body => information = body, requestQuestId: 0);
            FormalDungeonTcpTests.Load(peer); PartyDungeonTcpTests.Drain(peer);
            That(Remaining(first) == 0 && Remaining(second) == 7, "06:00前正常首次房间扣至0，不影响另一个账号");
            await clock.WaitForOneShotTimerAsync(localResetAt);
            clock.Advance(TimeSpan.FromSeconds(1));
            await clock.WaitForOneShotTimerAsync(legacyResetAt);
            foreach (var client in new[] { peer, other })
            {
                // 不发送屏障或心跳，直接等定时通知，证明持续在线且没有新请求也会刷新。
                var packet = client.Expect(0,36).Body;
                Bytes(packet, Build(w => { foreach (ushort value in new ushort[] { 0, 156, 23, 0, 11 }) w.Write(value); }),
                    "原生NOTI36即时同步新日疲劳，不改附属字段、不切换客户端状态");
                var notices = PartyDungeonTcpTests.Drain(client);
                That(!notices.Any(row => row is { Kind:0,Opcode:550 }) &&
                    notices.Any(row => row is { Kind:0,Opcode:1536,Body.Length:5 }),
                    "本机六点保留强袭在线推送，不提前刷新东八区日界的副本次数");
            }
            That(Remaining(first) == 156 && Remaining(second) == 156 &&
                fixture.Query("SELECT min(service_day),max(service_day) FROM character_fatigue_reset_days WHERE character_id IN($p0,$p1)", first, second) is [[long min, long max]] && min == day + 1 && max == day + 1,
                "在线副本和城镇两账号各恢复一次且日戳已提交");
            That(Tickets(first) == 3 && Tickets(second) == 5,
                "无客户端请求跨本地06:00，副本中有婚戒的角色由2补到3，另一账号已有5张不扣减");
            peer.Send(37, new byte[8]); PartyDungeonTcpTests.Drain(peer);
            That(Remaining(first) == 156, "跨日不清除已加载房间的疲劳去重记录");
            foreach (int at in Enumerable.Range(0, map[18]).Select(index => 19 + index * 22)
                .Where(at => map[at + 13] <= 3 && map[at + 21] == 0))
            {
                peer.Send(39, FormalDungeonTcpTests.Death(U16(map, at + 6), peer.Id, map[at + 12]));
                PartyDungeonTcpTests.Drain(peer);
            }
            var maze = SelfTestCatalogs.Dungeons(resources.Archive).Get(3).Mazes[information[7]];
            var current = new DfoServer.GameWorld.Dungeons.DungeonPoint(map[0], map[1]);
            var link = maze.StaticTopology.Links!.First(row => row.First == current || row.Second == current);
            var next = link.First == current ? link.Second : link.First;
            var move = new byte[100]; move[0] = checked((byte)next.X); move[1] = checked((byte)next.Y);
            peer.Send(45, move); peer.Expect(1, 45, [1]); peer.Expect(0, 29);
            FormalDungeonTcpTests.Load(peer); PartyDungeonTcpTests.Drain(peer);
            That(Remaining(first) == 155 && Remaining(second) == 156, "原实例跨日后仍可正常过图，新房间实扣1，不影响另一账号");

            await clock.WaitForOneShotTimerAsync(legacyResetAt);
            clock.Advance(TimeSpan.FromHours(16));
            foreach (var client in new[] { peer, other })
            {
                bool received = false;
                for (int count = 0; count < 200; count++)
                {
                    var notice = client.Read();
                    That(notice.Kind == 0 && notice.Opcode is not (36 or 1536), "东八区日界不重复刷新本机疲劳或强袭");
                    if (notice.Opcode != 550) continue;
                    That(notice.Body.Length == 6 && U32(notice.Body) > 0, "原生日界在线推送完整副本次数");
                    received = true;
                    break;
                }
                That(received, "无客户端请求时仍在原东八区日界推送副本次数");
                var later = PartyDungeonTcpTests.Drain(client);
                That(!later.Any(row => row is { Kind:0,Opcode:36 or 1536 }), "原日界刷新不触发本机日切通知");
            }
        }
        await clock.WaitForOneShotTimersAsync(0);
        using (var peer = Open(fixture))
        {
            peer.Select(1); peer.Barrier();
            That(fixture.Query("SELECT remaining FROM character_fatigue WHERE character_id=(SELECT id FROM characters WHERE name='DailyOffline')") is [[156L]],
                "未在线角色在选角时补齐日切，不要求06:00在线");
        }
        await clock.WaitForOneShotTimersAsync(0);
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "日切后存档完整，退出收尾不遗留时钟任务");
        long Remaining(ushort id) => Convert.ToInt64(fixture.Query("SELECT remaining FROM character_fatigue WHERE character_id=$p0", id).Single()[0]);
        long Tickets(ushort id) => Convert.ToInt64(fixture.Query("""
            SELECT COALESCE(SUM(json_extract(i.item_core,'$.Value')),0)
            FROM character_inventory_items i JOIN character_inventory_slots s ON s.item_uid=i.item_uid
            WHERE i.character_id=$p0 AND s.list_type=0 AND json_extract(i.item_core,'$.ItemId')=10002609
            """, id).Single()[0]);
    }
}
