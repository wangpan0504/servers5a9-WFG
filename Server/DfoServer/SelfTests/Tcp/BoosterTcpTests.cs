using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using DfoServer.Network.Management;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class BoosterTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        Inventory.BoosterTests.Run(resources.Towns, resources.Characters, resources.ItemResources);
        var itemResources = resources.ItemResources;
        var resourceWalletBox = itemResources.Boosters.Get(2683799);
        That(resourceWalletBox.Available && resourceWalletBox.Selection is null && resourceWalletBox.PackageRewards.IsDefaultOrEmpty &&
            resourceWalletBox.Pools is [{ Draws: 1, Rewards: [{ ItemId: 2681762, Weight: > 0, Count: > 0 }] }],
            "钱包礼盒TCP用例要求匹配资源具有唯一确定奖励");
        int resourceWalletCount = resourceWalletBox.Pools[0].Rewards[0].Count;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "booster"), gm: true, resources: resources);
        var characters = resources.Characters;
        var towns = resources.Towns;
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            repo.Create(owner, new("BoxTcp", towns.Room, towns.InitialReturn)
            {
                Job = 15, AcceptedStoryLevel = 1, Skills = characters.Get(15).Skills,
                Inventory = new([new(0, Bag(3), Core(2660291, 2)), new(0, Bag(4), Core(2683799)),
                    new(0, Bag(5), Core(10160255)), new(0, Bag(6), Core(2058)), new(0, Bag(7), Core(2058, int.MaxValue)),
                    new(0, Bag(65), Core(7542, 2)), new(0, Bag(79), Core(490704570))], Gold: 12345)
            });
            repo.Create(owner, new("SoulBox", towns.Room, towns.InitialReturn)
            {
                Job = 0, GrowType = 1, AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills,
                Inventory = new([new(0, Bag(65), Core(490704570))], Gold: 12345)
            });
            foreach (byte branch in new byte[] { 4, 0x25 })
                repo.Create(owner, new(branch == 4 ? "OtherBranch" : "FifthBranch", towns.Room, towns.InitialReturn)
                {
                    Job = 0, GrowType = branch, AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills,
                    Inventory = new([new(0, Bag(65), InventoryRewardGrantService.Create(itemResources.Items.Get(490011456), shopExpiration: true))])
                });
            var unsealSource = itemResources.Items.Get(10012169);
            byte unsealJob = (byte)Enumerable.Range(0, 16).First(job => (unsealSource.Jobs & (1 << job)) != 0);
            repo.Create(owner, new("UnsealBox", towns.Room, towns.InitialReturn)
            {
                Job = unsealJob, Level = Math.Max((byte)1, unsealSource.MinimumLevel), AcceptedStoryLevel = 1, Skills = characters.Get(unsealJob).Skills,
                Inventory = new([new(0, Bag(65), InventoryRewardGrantService.Create(unsealSource, 2))], Gold: 12345)
            });
        }
        await fixture.StartAsync();
        SetExpirationDebug(fixture, false);
        using (var peer = Open(fixture))
        {
            SendOpen(peer, 65, 1);
            peer.Expect(1, 160, [0, 19, 0]);
            peer.Select();
            string before = Snapshot();
            SendOpen(peer, 79, 1);
            peer.Expect(1, 160, [0, 19, 0]);
            foreach (int count in new[] { 0, -1, 3, int.MaxValue })
            {
                SendOpen(peer, 65, count);
                peer.Expect(1, 160, [0, 19, 0]);
            }
            peer.Send(160, new byte[9]);
            peer.Barrier();
            That(before == Snapshot(), "invalid count, pending selection and unmatched grow-type box do not consume or fabricate success");
            for (int i = 1; i <= 2; i++)
            {
                SendOpen(peer, 65, 1);
                ExpectAck(peer, 7542, 65, [(1108, 20), (1114, 20), (1016, 20)]);
                byte[] delta = peer.Expect(0, 14).Body;
                That(U16(delta, 1) == 4, "one box and three complete pool results are projected after ACK");
                Entry(delta, 65, i == 1 ? 7542 : -1, i == 1 ? 1 : 0);
                Entry(delta, 66, 1108, i * 20);
                Entry(delta, 67, 1114, i * 20);
                Entry(delta, 68, 1016, i * 20);
            }
            before = Snapshot();
            SendOpen(peer, 65, 1);
            peer.Expect(1, 160, [0, 19, 0]);
            peer.Barrier();
            That(before == Snapshot(), "replaying consumed source cannot produce another reward");
            SendOpen(peer, 3, 2);
            ExpectAck(peer, 2660291, 3, [(1, 20)]);
            byte[] coinDelta = peer.Expect(0, 14).Body;
            Entry(coinDelta, 3, -1, 0);
            Entry(coinDelta, 1, 1, 20);
            SendOpen(peer, 4, 1);
            ExpectAck(peer, 2683799, 4, [(2681762, resourceWalletCount)]);
            Entry(peer.Expect(0, 14).Body, 4, -1, 0);
            peer.Expect(0, 53, Build(writer => { writer.Write((byte)1); writer.Write(0); writer.Write(resourceWalletCount); writer.Write(0); }));
            SendOpen(peer, 5, 1);
            ExpectAck(peer, 10160255, 5, [(10158124, 1)]);
            byte[] soulDelta = peer.Expect(0, 14).Body;
            Entry(soulDelta, 5, -1, 0);
            Entry(soulDelta, 365, 10158124, 1);
            SendOpen(peer, 6, 1);
            byte[] randomAck = peer.Expect(1, 160).Body;
            That(randomAck.Length == 34 && randomAck[0] == 1 && U32(randomAck, 2) == 2058 && U16(randomAck, 6) == 6 && U16(randomAck, 16) == 2,
                "random box uses the same full native header");
            int first = (int)U32(randomAck, 18), second = (int)U32(randomAck, 26);
            That(first is >= 2050 and <= 2053 && second is >= 2054 and <= 2057 && U32(randomAck, 22) == 1 && U32(randomAck, 30) == 1,
                "one actual reward from each independent random pool is displayed");
            byte[] emblemDelta = peer.Expect(0, 14).Body;
            Entry(emblemDelta, 6, -1, 0);
            Entry(emblemDelta, 289, first, 1);
            Entry(emblemDelta, 290, second, 1);
            peer.Barrier();
            That(fixture.Query("SELECT count(*),count(DISTINCT item_uid) FROM character_inventory_items") is [[11L, 11L]] &&
                fixture.Query("SELECT count(DISTINCT character_id) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=490704570") is [[2L]],
                "原有八个实例加两件首转资格礼盒共有十个唯一实例，原有两个转职奖池礼盒仍在");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            SendOpen(peer, 7, int.MaxValue);
            byte[] bulkAck = peer.Expect(1, 160).Body;
            That(bulkAck[0] == 1 && U32(bulkAck, 2) == 2058 && U16(bulkAck, 6) == 7 && U16(bulkAck, 16) == 8 && bulkAck.Length == 82,
                "largest valid batch receives the normal success receipt without a new count restriction");
            byte[] bulkDelta = peer.Expect(0, 14).Body;
            timer.Stop();
            long[] poolTotals = new long[2];
            Entry(bulkDelta, 7, -1, 0);
            for (int i = 0; i < 8; i++)
            {
                int id = (int)U32(bulkAck, 18 + i * 8), count = (int)U32(bulkAck, 22 + i * 8);
                That(id is >= 2050 and <= 2057 && count > 0, "large batch receipt retains valid reward IDs and counts");
                poolTotals[(id - 2050) / 4] += count;
                int at = Enumerable.Range(0, U16(bulkDelta, 1)).Select(index => 3 + index * 123).Single(index => U32(bulkDelta, index + 2) == id);
                That(U32(bulkDelta, at + 6) == count + (id == first || id == second ? 1 : 0), "bulk delta equals actual reward plus previous stack");
            }
            That(poolTotals.All(total => total == int.MaxValue), "both pools retain exactly the requested number of independent draws");
            peer.Barrier();
            Console.WriteLine($"Bulk booster TCP ACK and delta: {timer.Elapsed.TotalMilliseconds:F2}ms");
        }
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repo.List(owner).Single(role => role.Name == "BoxTcp");
            InventoryService.Commit(repo, owner, current, InventoryRewardGrantService.TryGrantBatch(current, [new(2660291, 1)], itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces));
        }
        using (var peer = Open(fixture))
        {
            byte[] full = peer.Select()[4].Body;
            Entry(full, 1, 1, 20, true);
            Entry(full, 365, 10158124, 1, true);
            Entry(full, 66, 1108, 40, true);
            Entry(full, 65, 2660291, 1, true);
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_booster BEFORE INSERT ON currency_audit_log BEGIN SELECT RAISE(ABORT,'booster rollback'); END");
            SendOpen(peer, 65, 1);
            peer.Closed();
            That(before == Snapshot(), "commit failure returns no success and restores source, wallets, revisions, audit and UID sequence");
            fixture.Sql("DROP TRIGGER fail_booster");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            SendOpen(peer, 65, 1);
            ExpectAck(peer, 2660291, 65, [(1, 10)]);
            byte[] delta = peer.Expect(0, 14).Body;
            Entry(delta, 65, -1, 0);
            Entry(delta, 1, 1, 30);
            peer.Barrier();
            peer.Send(160, new byte[5]);
            peer.Expect(1, 160, [0, 19, 0]); // master #327: truncated CMD160 now gets a failure reply and keeps the connection
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            byte[] restored = peer.Select()[4].Body;
            Entry(restored, 1, 1, 30, true);
            Entry(restored, 79, 490704570, 1, true);
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.Select(1);
            SendOpen(peer, 65, 1);
            byte[] ack = peer.Expect(1, 160).Body;
            That(ack.Length == 26 && ack[0] == 1 && ack[1] == 0 && U32(ack, 2) == 490704570 && U16(ack, 6) == 65 &&
                U16(ack, 16) == 1 && U32(ack, 22) == 1, "灵魂武器礼盒成功ACK保留普通18B头和单件奖励");
            int reward = (int)U32(ack, 18);
            That(itemResources.Items.Get(reward).Kind == ItemKind.Equipment, "转职池发放实际装备而不是钱包或残缺实例");
            byte[] delta = peer.Expect(0, 14).Body;
            Entry(delta, 65, -1, 0);
            That(Enumerable.Range(0, U16(delta, 1)).Select(index => 3 + index * 123)
                    .Any(at => U32(delta, at + 2) == reward && U32(delta, at + 6) == InventoryRewardGrantService.InitialQuality),
                "装备差量出现在ACK之后且保留完整品质字段");
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            byte[] full = peer.Select(1)[4].Body;
            That(Enumerable.Range(0, U16(full, 3)).Select(index => 5 + index * 123).All(at => U16(full, at) != 65 ||
                    U32(full, at + 2) == 0), "重登后源礼盒已扣除");
            That(Enumerable.Range(0, U16(full, 3)).Select(index => 5 + index * 123).Any(at =>
                    itemResources.Boosters.Get(490704570).Pools.Any(pool =>
                        pool.Rewards.Any(row => row.ItemId == (int)U32(full, at + 2)))),
                "重登恢复灵魂武器装备");
            peer.Barrier();
        }
        SetExpirationDebug(fixture, true);
        using (var peer = Open(fixture))
        {
            peer.Select(2);
            string unchanged = Snapshot();
            SendOpen(peer, 65, 1);
            peer.Expect(1, 160, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == unchanged, "错误首转分支的真实CMD160拒绝且资产不变");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(3);
            SendOpen(peer, 65, 1);
            ExpectAck(peer, 490011456, 65, [(10305616, 200)]);
            byte[] delta = peer.Expect(0, 14).Body;
            Entry(delta, 65, -1, 0);
            That(Enumerable.Range(0, U16(delta, 1)).Select(index => 3 + index * 123)
                .Any(at => U32(delta, at + 2) == 10305616 && U32(delta, at + 6) == 200),
                "第五分支觉醒角色获得真实200碎片完整差量");
            peer.Barrier();
            string consumed = Snapshot();
            SendOpen(peer, 65, 1);
            peer.Expect(1, 160, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == consumed, "重放已消费的首转礼盒不发第二份奖励");
        }
        byte[][] unsealedRows;
        using (var peer = Open(fixture))
        {
            peer.Select(4);
            SendOpen(peer, 65, 2);
            var ack = peer.Expect(1, 160).Body;
            That(ack.Length == 90 && ack[0] == 1 && U32(ack, 2) == 10012169 && U16(ack, 16) == 9,
                "真实CMD160九件套两盒显示九种各两件奖励");
            var delta = peer.Expect(0, 14).Body;
            unsealedRows = Enumerable.Range(0, U16(delta, 1)).Select(index => 3 + index * 123)
                .Where(at => U16(delta, at) is >= 9 and <= 56).Select(at => delta[at..(at + 123)]).ToArray();
            That(unsealedRows.Length == 18 && unsealedRows.All(row => row[71] is >= 1 and <= 3 && row[72] != 0 && row[82] == 255),
                "18件完整装备差量分别携带已解封词条");
            peer.Barrier();
            string saved = Snapshot();
            SendOpen(peer, 65, 2); peer.Expect(1, 160, [0, 19, 0]); peer.Barrier();
            That(Snapshot() == saved, "解封盒源耗尽重放不重复发奖或重抽");
        }
        using (var peer = Open(fixture))
        {
            var full = peer.Select(4)[4].Body;
            foreach (var row in unsealedRows)
            {
                int at = Enumerable.Range(0, U16(full, 3)).Select(index => 5 + index * 123).Single(at => U16(full, at) == U16(row, 0));
                That(full[at..(at + 123)].SequenceEqual(row), "重登全量逐字节保留各件已冻结随机核心");
            }
            peer.Barrier();
        }
        That(fixture.Query("SELECT gold FROM characters WHERE slot=4") is [[12345L]], "免费礼盒解封不扣普通解封金币");
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repo.Create(owner, new("PurgedBox", towns.Room, towns.InitialReturn)
            {
                Job = 12, GrowType = 35, Level = 95, AcceptedStoryLevel = 1, Skills = characters.Get(12).Skills,
                Inventory = new([new(0, Bag(69), Core(10309039, 120))])
            });
        using (var peer = Open(fixture))
        {
            peer.Select(5);
            // 不持有兑换材料；[need material] 不是使用费用。
            SendOpen(peer, 69, 1);
            ExpectAck(peer, 10309039, 69, [(10305892, 1)]);
            byte[] delta = peer.Expect(0, 14).Body;
            Entry(delta, 69, 10309039, 119);
            short rewardSlot = (short)U16(delta, Enumerable.Range(0, U16(delta, 1)).Select(i => 3 + i * 123)
                .Single(at => U32(delta, at + 2) == 10305892));
            Entry(delta, rewardSlot, 10305892, 1);
            SendOpen(peer, 69, 119);
            ExpectAck(peer, 10309039, 69, [(10305892, 119)]);
            delta = peer.Expect(0, 14).Body;
            Entry(delta, 69, -1, 0);
            Entry(delta, rewardSlot, 10305892, 120);
            peer.Barrier();
            string consumed = Snapshot();
            SendOpen(peer, 69, 1);
            peer.Expect(1, 160, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == consumed, "黎明结晶耗尽后重放不重复发奖");
        }
        using (var peer = Open(fixture))
        {
            byte[] full = peer.Select(5)[4].Body;
            That(Enumerable.Range(0, U16(full, 3)).Select(i => 5 + i * 123)
                .Any(at => U32(full, at + 2) == 10305892 && U32(full, at + 6) == 120),
                "重连恢复黎明结晶单开及批量开启的完整奖励数量");
            peer.Barrier();
        }
        await fixture.StopAsync();
        That(fixture.Query("SELECT gold FROM characters WHERE slot=0") is [[12345L]], "ordinary boxes never infer an extra gold fee");
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0, "booster database integrity");

        ItemCore Core(int id, int count = 1) => InventoryRewardGrantService.Create(itemResources.Items.Get(id), count);
        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT revision,gold,revive_coins FROM characters")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT revision FROM account_wallets")) +
            JsonSerializer.Serialize(fixture.Query("SELECT currency_kind,balance FROM account_currency_balances ORDER BY currency_kind")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM currency_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'"));
    }
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    internal static void SetExpirationDebug(TcpTestFixture fixture, bool enabled)
    {
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "cash-shop-expiration " + (enabled ? "on" : "off"), out string reply) &&
            reply == "OK {\"ignoreAbsoluteExpiration\":" + (enabled ? "true" : "false") + "}", "GM设置实际礼包期限调试状态");
    }
    static void SendOpen(TcpPeer peer, short slot, int count) => peer.Send(160, Build(writer => { writer.Write(slot); writer.Write(count); }));
    internal static void ExpectAck(TcpPeer peer, int source, short slot, (int Id, int Count)[] rewards) => peer.Expect(1, 160, Build(writer =>
    {
        writer.Write((byte)1);
        writer.Write((byte)0);
        writer.Write(source);
        writer.Write(slot);
        writer.Write(0);
        writer.Write(0);
        writer.Write((ushort)rewards.Length);
        foreach (var reward in rewards)
        {
            writer.Write(reward.Id);
            writer.Write(reward.Count);
        }
    }));
    internal static void Entry(byte[] body, short slot, int item, int value, bool full = false)
    {
        int header = full ? 5 : 3;
        int count = U16(body, full ? 3 : 1);
        That(body[0] == 0 && body.Length == header + count * 123, "complete native inventory entry width");
        int at = Enumerable.Range(0, count).Select(index => header + index * 123).Single(at => U16(body, at) == slot);
        That(U32(body, at + 2) == unchecked((uint)item) && U32(body, at + 6) == value, "committed booster slot " + slot);
    }
}
