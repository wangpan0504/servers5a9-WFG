using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class LotteryTcpTests
{
    static async Task GoldCostAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        const int pot = 2749786;
        var items = resources.ItemResources;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "lottery-gold-cost"), resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repo.Create(owner, new("GoldCost", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, Bag(117), InventoryRewardGrantService.Create(items.Items.Get(pot), 2))], Gold: 1999)
            });
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            string before = Snapshot();
            Send(peer, 117);
            peer.Expect(1, 27, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == before, "金币差一真实CMD27拒绝，源、金币、奖励、审计和UID不变");
        }
        // 隔离夹具补足两次费用；不创建材料，不能把购买价当成使用价。
        fixture.Sql("UPDATE characters SET gold=4000");
        using (var peer = Open(fixture))
        {
            peer.Select();
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_gold_cost BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'gold cost rollback'); END");
            Send(peer, 117);
            peer.Closed();
            That(Snapshot() == before, "收费罐晚SQL失败原子保留金币、源、完整奖励、审计和UID");
            fixture.Sql("DROP TRIGGER fail_gold_cost");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            for (int opened = 1; opened <= 2; opened++)
            {
                Send(peer, 117);
                byte[] ack = peer.Expect(1, 27).Body;
                int id = (int)U32(ack, 5);
                short slot = (short)U16(ack, 3);
                That(ack.Length == 130 && ack[0] == 1 && U16(ack, 1) == 117 && U32(ack, 126) == 0 &&
                    items.Boosters.Get(pot).Pools.Single().Rewards.Any(row => row.ItemId == id),
                    "收费罐真实ACK27仍为原奖池完整实物记录，金币奖励段为零");
                byte[] delta = peer.Expect(0, 14).Body;
                BoosterTcpTests.Entry(delta, 117, opened == 1 ? pot : -1, opened == 1 ? 1 : 0);
                BoosterTcpTests.Entry(delta, 0, 0, 4000 - opened * 2000);
                int at = Enumerable.Range(0, U16(delta, 1)).Select(index => 3 + index * 123)
                    .Single(index => U16(delta, index) == slot);
                That(ack.AsSpan(3, 123).SequenceEqual(delta.AsSpan(at, 123)),
                    "费用扣除后ACK装备核心与权威差量逐字节一致");
                peer.Barrier();
                That(Convert.ToInt64(fixture.Query("SELECT gold FROM characters").Single()[0]) == 4000 - opened * 2000 &&
                    fixture.Query("SELECT count(*) FROM character_inventory_items") is [[2L]],
                    "每开一次只扣2000金币，源与完整装备共同落库且不生成购买材料");
            }
            string exhausted = Snapshot();
            Send(peer, 117);
            peer.Expect(1, 27, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == exhausted, "收费罐耗尽重放不扣钱或重复发奖");
        }
        string committed = Snapshot();
        await fixture.StopAsync();
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Barrier();
            That(Snapshot() == committed && fixture.Query("PRAGMA integrity_check") is [["ok"]] &&
                fixture.Query("PRAGMA foreign_key_check").Length == 0,
                "重启与重登保留收费结果、完整实例和UID，数据库完整");
        }
        await fixture.StopAsync();
        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT revision,gold FROM characters")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'"));
    }
}
