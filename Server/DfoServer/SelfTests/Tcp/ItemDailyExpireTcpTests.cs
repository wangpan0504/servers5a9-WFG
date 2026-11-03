using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class ItemDailyExpireTcpTests
{
    const int Daily = 490705058;

    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        DailyItemExpirationTests.RunCore();
        DailyItemExpirationTests.RunResources(resources.Towns, resources.ItemResources);
        DailyItemExpirationTests.RunGrant(resources.ItemResources, resources.Archive, resources.Quests);
        WeeklyItemExpirationTests.RunCore();
        WeeklyItemExpirationTests.RunResources(resources.Towns, resources.ItemResources);

        var zone = TimeZoneInfo.CreateCustomTimeZone("item-expire-utc8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
        var clock = new DailyBoosterTestClock(DateTimeOffset.Parse("2026-10-18T21:59:59Z"), zone);
        var now = clock.GetUtcNow();
        var local = now.ToOffset(TimeSpan.FromHours(8));
        var todaySix = new DateTimeOffset(local.Year, local.Month, local.Day, 6, 0, 0, local.Offset);
        var boundaryTime = todaySix <= now ? todaySix.AddDays(1) : todaySix;
        int boundary = checked((int)boundaryTime.ToUnixTimeSeconds());
        int later = checked((int)boundaryTime.AddDays(1).ToUnixTimeSeconds());
        That(DfoServer.GameWorld.Time.ServiceDay.NextReset(now, zone).ToUnixTimeSeconds() == boundary,
            "日切钩子与道具 daily 截止对齐到同一 UTC+8 06:00");

        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "item-daily-expire"),
            resources: resources, clock: clock);
        var repository = new CharacterRepository(fixture.Database);
        var daily = resources.ItemResources.Items.Get(Daily);
        InventorySlot expiredSlot;
        ushort onlineId, selectId;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("ExpireOnline", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get);
            var online = candidate.Inventory;
            (online, expiredSlot) = Place(online, daily, boundary, now);
            (online, _) = Place(online, daily, 0, now);
            (online, _) = Place(online, daily, later, now);
            // 2749724 在 refill item Count=0，选角/06:00 的 server_item_cycle 会删，不能当 ExpireInventory 对照。
            // 无字段 period:off 仍为 0 由 DailyItemExpirationTests 覆盖。
            onlineId = repository.Create(owner, candidate with { Inventory = online }).Id;
            var offline = candidate.Inventory;
            (offline, _) = Place(offline, daily, 1, now);
            (offline, _) = Place(offline, daily, 0, now);
            selectId = repository.Create(owner, candidate with { Name = "ExpireSelect", Inventory = offline }).Id;
        }

        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Barrier();
            That(CountBag(fixture, onlineId, Daily, boundary) == 1 &&
                CountBag(fixture, onlineId, Daily, 0) == 1 &&
                CountBag(fixture, onlineId, Daily, later) == 1,
                "06:00 前在线不清未到期或 ExpireTime=0 实例");
            That(fixture.Query("SELECT count(*) FROM inventory_audit_log WHERE action_name='item_expired' AND character_id=$p0", onlineId) is [[0L]],
                "到期前日切钩子不写 item_expired");

            await clock.WaitForOneShotTimerAsync(boundaryTime);
            clock.Advance(TimeSpan.FromSeconds(1));
            bool cleared = false;
            for (int count = 0; count < 200; count++)
            {
                var notice = peer.Read();
                if (notice is { Kind: 0, Opcode: 14 } && ClearsSlot(notice.Body, expiredSlot))
                {
                    cleared = true;
                    break;
                }
            }
            That(cleared, "在线跨 UTC+8 06:00 下发 opcode 14 清掉已到期格");
            PartyDungeonTcpTests.Drain(peer);
            That(CountBag(fixture, onlineId, Daily, boundary) == 0 &&
                CountBag(fixture, onlineId, Daily, 0) == 1 &&
                CountBag(fixture, onlineId, Daily, later) == 1,
                "日切钩子只删已到期 daily 实例；未到期与 ExpireTime=0 不动（A 不追溯）");
            That(fixture.Query("SELECT count(*) FROM inventory_audit_log WHERE action_name='item_expired' AND character_id=$p0", onlineId) is [[1L]],
                "在线清包审计 item_expired");
        }

        using (var peer = Open(fixture))
        {
            peer.Select(1);
            peer.Barrier();
            That(CountBag(fixture, selectId, Daily, 1) == 0 && CountBag(fixture, selectId, Daily, 0) == 1,
                "选角删除已过期实例，daily 模板 + ExpireTime=0 保持永久（A 不追溯）");
            That(fixture.Query("SELECT count(*) FROM inventory_audit_log WHERE action_name='item_expired' AND character_id=$p0", selectId) is [[1L]],
                "选角兜底同样审计 item_expired");
        }

        await clock.WaitForOneShotTimersAsync(0);
        await fixture.StopAsync();
        Console.WriteLine("PASS C# TCP item-daily-expire: GM ClearedExpire、在线 06:00 清包、选角兜底、A 不追溯");
    }

    static (InventorySnapshot Inventory, InventorySlot Slot) Place(InventorySnapshot inventory, DfoServer.GameWorld.Items.ItemTemplate template, int expire, DateTimeOffset now)
    {
        var list = ItemSlotBoundService.List(template.Kind);
        short index = ItemSlotBoundService.Range(template.Kind, inventory).Start;
        while (inventory.Find(new(list, index)) is not null) index++;
        var slot = new InventorySlot(list, index);
        var core = InventoryRewardGrantService.Create(template, 1, now) with { ExpireTime = expire };
        return (inventory with { Items = inventory.Items.Add(new(0, slot, core)) }, slot);
    }

    static long CountBag(TcpTestFixture fixture, ushort characterId, int itemId, int expire) =>
        Convert.ToInt64(fixture.Query("""
            SELECT count(*) FROM character_inventory_items i
            JOIN character_inventory_slots s USING(item_uid)
            WHERE i.character_id=$p0 AND json_extract(i.item_core,'$.ItemId')=$p1
              AND json_extract(i.item_core,'$.ExpireTime')=$p2
              AND NOT EXISTS (SELECT 1 FROM mailbox_attachments a WHERE a.item_uid=i.item_uid)
            """, characterId, itemId, expire).Single()[0]);

    static bool ClearsSlot(byte[] body, InventorySlot slot)
    {
        if (body.Length < 3 || body[0] != (byte)slot.List) return false;
        int count = U16(body, 1);
        for (int i = 0; i < count; i++)
        {
            int at = 3 + i * 123;
            if (at + 6 > body.Length) return false;
            if (U16(body, at) == (ushort)slot.Index && U32(body, at + 2) == uint.MaxValue)
                return true;
        }
        return false;
    }
}
