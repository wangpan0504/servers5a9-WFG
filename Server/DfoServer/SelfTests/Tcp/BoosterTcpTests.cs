using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
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
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repo.List(owner).Single(role => role.Name == "SoulBox");
            InventoryService.Commit(repo, owner, current, InventoryRewardGrantService.TryGrantBatch(current,
                [new(10002631, 1)], itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces));
        }
        (int Id, int Count)[] weddingRewards = [(2683422, 1), (101600020, 1), (10002901, 100), (10002611, 1), (10002612, 1)];
        using (var peer = Open(fixture))
        {
            byte[] full = peer.Select(1)[4].Body;
            short slot = (short)U16(full, Enumerable.Range(0, U16(full, 3)).Select(i => 5 + i * 123)
                .Single(at => U32(full, at + 2) == 10002631));
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_wedding_box BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'wedding box rollback'); END");
            SendOpen(peer, slot, 1);
            peer.Closed();
            That(Snapshot() == before, "职业婚礼箱SQL失败保留源、全部奖励及审计，不提前成功");
            fixture.Sql("DROP TRIGGER fail_wedding_box");
        }
        using (var peer = Open(fixture))
        {
            byte[] full = peer.Select(1)[4].Body;
            short slot = (short)U16(full, Enumerable.Range(0, U16(full, 3)).Select(i => 5 + i * 123)
                .Single(at => U32(full, at + 2) == 10002631));
            SendOpen(peer, slot, 1);
            ExpectAck(peer, 10002631, slot, weddingRewards);
            Entry(peer.Expect(0, 14).Body, slot, -1, 0);
            byte[] delta = peer.Expect(0, 14).Body;
            foreach (var reward in weddingRewards.Where(reward => reward.Id != 101600020))
                That(Enumerable.Range(0, U16(delta, 1)).Select(i => 3 + i * 123)
                    .Any(at => U32(delta, at + 2) == reward.Id && U32(delta, at + 6) == reward.Count),
                    "职业婚礼箱普通背包差量包含完整奖励 " + reward.Id);
            byte[] avatar = peer.Expect(0, 14).Body;
            That(avatar[0] == 1 && U16(avatar, 1) == 1 && avatar.Length == 168 && U32(avatar, 5) == 101600020,
                "婚礼武器装扮通过独立容器发完整165B实例");
            peer.Barrier();
            string consumed = Snapshot();
            SendOpen(peer, slot, 1);
            peer.Expect(1, 160, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == consumed, "婚礼箱源耗尽后不能重复发奖");
        }
        using (var peer = Open(fixture))
        {
            byte[] full = peer.Select(1)[4].Body;
            foreach (var reward in weddingRewards.Where(reward => reward.Id != 101600020))
                That(Enumerable.Range(0, U16(full, 3)).Select(i => 5 + i * 123)
                    .Any(at => U32(full, at + 2) == reward.Id && U32(full, at + 6) == reward.Count),
                    "重连恢复婚礼箱普通背包完整奖励 " + reward.Id);
            byte[] avatar = peer.SpecialContainers[1].Body;
            That(avatar[0] == 1 && U16(avatar, 3) == 1 && avatar.Length == 170 && U32(avatar, 7) == 101600020,
                "重连从装扮容器恢复婚礼武器装扮完整实例");
            That(!Enumerable.Range(0, U16(full, 3)).Select(i => 5 + i * 123).Any(at => U32(full, at + 2) == 10002631),
                "重连后不恢复已消费婚礼箱");
            peer.Barrier();
        }
        await RestrictedOrdinaryAsync(fixture, resources, repo, owners);
        CharacterPool(fixture,resources,repo,owners);
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
    static async Task RestrictedOrdinaryAsync(TcpTestFixture fixture, TcpTestResources resources, CharacterRepository repo, AccountOwners owners)
    {
        Inventory.BoosterDisjointInstanceTests.RunOrdinary(resources.Towns, resources.ItemResources);
        var items = resources.ItemResources;
        var actors = new List<(Character Actor, int Source, InventoryItem Sentinel)>();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        foreach (int id in new[] { 490014072, 490014073, 490704552, 490704553 })
        {
            int first = items.Boosters.Get(id).Pools[0].Rewards[0].ItemId;
            var now = DateTimeOffset.UtcNow;
            var actor = repo.Create(owner, CharacterCreation.Prepare("RestBox" + actors.Count, resources.Characters.Get(0),
                resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get) with
            {
                AcceptedStoryLevel = 1,
                Inventory = new([new(0, Bag(65), InventoryRewardGrantService.Create(items.Items.Get(id), utcNow: now)),
                    new(0, Bag(9), InventoryRewardGrantService.Create(items.Items.Get(first), utcNow: now))], Gold: 12345)
            });
            actors.Add((actor, id, actor.Inventory.Find(Bag(9))!));
        }
        foreach (var entry in actors)
        {
            if (entry.Source == 490014072)
            {
                using var peer = Open(fixture);
                peer.Select(entry.Actor.Slot); peer.Barrier();
                string before = State();
                fixture.Sql("CREATE TRIGGER fail_restricted_booster BEFORE INSERT ON inventory_audit_log " +
                    "WHEN NEW.action_name='booster_open' BEGIN SELECT RAISE(ABORT,'restricted booster rollback'); END");
                SendOpen(peer, 65, 1); peer.Closed();
                That(State() == before, "普通限制三件套晚SQL失败回滚源、本次完整核心/期限、UID、revision与审计");
                fixture.Sql("DROP TRIGGER fail_restricted_booster");
            }
            using var client = Open(fixture);
            client.Select(entry.Actor.Slot); client.Barrier();
            long earliest = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
            SendOpen(client, 65, 1);
            var expectedRewards = items.Boosters.Get(entry.Source).Pools.Select(pool => (Id: pool.Rewards[0].ItemId, Count: 1)).ToArray();
            ExpectAck(client, entry.Source, 65, expectedRewards);
            var delta = client.Expect(0, 14).Body;
            Entry(delta, 65, -1, 0);
            client.Barrier();
            var current = Read(entry.Actor.Slot);
            var outputs = current.Inventory.Items.Where(item => item.Uid != entry.Sentinel.Uid).ToArray();
            That(current.Inventory.Find(Bag(65)) is null && current.Inventory.Find(Bag(9)) == entry.Sentinel &&
                current.Inventory.Gold == 12345 && outputs.Length == 3 && outputs.All(item => item.Uid > 0) &&
                outputs.Select(item => item.Core.ItemId).Order().SequenceEqual(expectedRewards.Select(row => row.Id).Order()),
                "真实CMD160整套三件落库且保留普通同模板哨兵和金币，源耗尽 " + entry.Source);
            foreach (var output in outputs)
            {
                var createdAt = DateTimeOffset.FromUnixTimeSeconds(output.Core.ExpireTime).AddDays(-30);
                var expected = InventoryRewardGrantService.Create(items.Items.Get(output.Core.ItemId), utcNow: createdAt)
                    with { DisjointForbidden = true };
                That(output.Core == expected && output.Core.ExpireTime >= earliest &&
                    output.Core.ExpireTime <= DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds(),
                    "实际十二件完整核心及30天期限，只带分解限制且无毁装灵魂限制 " + output.Core.ItemId);
                Entry(delta, output.Position.Index, output.Core.ItemId, output.Core.Value);
                string committed = State();
                client.Send(26, Build(writer => { writer.Write(output.Position.Index); writer.Write((byte)0); writer.Write((short)-1); writer.Write(0); }));
                client.Expect(1, 26, [0, 19, 0]); client.Expect(0, 14); client.Barrier();
                That(State() == committed, "实际普通礼包限期装备拒分解且资产不变 " + output.Core.ItemId);
            }
            string consumed = State();
            SendOpen(client, 65, 1); client.Expect(1, 160, [0, 19, 0]); client.Barrier();
            That(State() == consumed, "耗尽普通礼包重放不发第二份装备 " + entry.Source);
        }
        var allActors = actors.Select(entry => entry.Actor).Concat(OrdinaryParents(fixture, resources, repo, owners)).ToArray();
        var inventories = allActors.ToDictionary(actor => actor.Slot, actor => JsonSerializer.Serialize(Read(actor.Slot).Inventory));
        await fixture.StopAsync();
        await fixture.StartAsync("ordinary-restricted-restart");
        foreach (var actor in allActors)
        {
            using var client = Open(fixture);
            client.Select(actor.Slot); client.Barrier();
            That(JsonSerializer.Serialize(Read(actor.Slot).Inventory) == inventories[actor.Slot],
                "普通礼包与达成子盒重启重登保持完整实例/限制/期限，不续期或复活源 " + actor.Slot);
        }
        Character Read(ushort slot)
        {
            using var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { });
            return repo.LoadCharacter(owner, slot);
        }
        string State() => fixture.Snapshot() + fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") + fixture.Snapshot("SELECT * FROM sqlite_sequence ORDER BY name");
    }

    static Character[] OrdinaryParents(TcpTestFixture fixture, TcpTestResources resources, CharacterRepository repo, AccountOwners owners)
    {
        var items = resources.ItemResources;
        var actors = new List<Character>();
        foreach (int sourceId in new[] { 490011382, 490012852 })
        {
            Character actor;
            using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
                actor = repo.Create(owner, CharacterCreation.Prepare("ParentBox" + actors.Count, resources.Characters.Get(0),
                    resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get) with
                {
                    AcceptedStoryLevel = 1,
                    Inventory = new([new(0, Bag(3), InventoryRewardGrantService.Create(items.Items.Get(sourceId))),
                        new(0, Bag(65), InventoryRewardGrantService.Create(items.Items.Get(1106)))], Gold: 12345)
                });
            actors.Add(actor);
            if (sourceId == 490011382)
            {
                using var peer = Open(fixture);
                peer.Select(actor.Slot); peer.Barrier();
                string before = State();
                fixture.Sql("CREATE TRIGGER fail_parent_booster BEFORE INSERT ON inventory_audit_log " +
                    "WHEN NEW.action_name='booster_open' BEGIN SELECT RAISE(ABORT,'parent booster rollback'); END");
                SendOpen(peer, 3, 1); peer.Closed();
                That(State() == before, "普通父盒发双子盒晚SQL失败回滚源、完整核心、UID序列、revision及审计");
                fixture.Sql("DROP TRIGGER fail_parent_booster");
            }
            using var client = Open(fixture);
            client.Select(actor.Slot); client.Barrier();
            var sentinel = Read(actor.Slot).Inventory.Find(Bag(65));
            SendOpen(client, 3, 1);
            var expected = items.Boosters.Get(sourceId).Pools.Select(pool => (Id: pool.Rewards[0].ItemId, Count: 1)).ToArray();
            ExpectAck(client, sourceId, 3, expected);
            var delta = client.Expect(0, 14).Body;
            Entry(delta, 3, -1, 0); client.Barrier();
            var received = Read(actor.Slot);
            var children = received.Inventory.Items.Where(item => expected.Any(row => row.Id == item.Core.ItemId)).ToArray();
            That(received.Inventory.Find(Bag(3)) is null && received.Inventory.Find(Bag(65)) == sentinel &&
                received.Inventory.Gold == 12345 && children.Length == 2,
                "真实普通父盒CMD160同时保存两份子盒，源只消费一次且保留哨兵和金币 " + sourceId);
            foreach (var child in children)
            {
                That(child.Uid > 0 && child.Core == InventoryRewardGrantService.Create(items.Items.Get(child.Core.ItemId)) &&
                    child.Core is { Kind: ItemKind.Consumable, DisjointForbidden: false, DestructionSoulForbidden: false,
                        EpicExchangeOrigin: null, Extension119: 0 },
                    "四子盒以自身完整核心落库，不代开、不带父盒装备限制或来源 " + child.Core.ItemId);
                Entry(delta, child.Position.Index, child.Core.ItemId, 1);
                var selection = items.Boosters.Get(child.Core.ItemId).Selection!;
                var category = selection.Categories.First(row => row.PendingRule is null && !row.Rewards.IsEmpty);
                var chosen = selection.SelectionCount == 0 ? category.Rewards.ToArray() : category.Rewards.Take(selection.SelectionCount).ToArray();
                BoosterSelectionTcpTests.SendSelect(client, child.Position.Index, chosen.Select(row => row.ItemId).ToArray(),
                    category.Category, category.Subcategory);
                ExpectAck(client, child.Core.ItemId, child.Position.Index, chosen.Select(row => (row.ItemId, row.Count)).ToArray());
                client.Expect(0, 14); client.Barrier();
                var opened = Read(actor.Slot);
                That(opened.Inventory.Find(child.Position) is null && chosen.All(row => opened.Inventory.Items.Any(item =>
                    item.Core.ItemId == row.ItemId && item.Core.DisjointForbidden && !item.Core.DestructionSoulForbidden &&
                    item.Core.Upgrade == row.Upgrade && item.Core.GenuineUpgrade == row.Refinement)),
                    "四个实际子盒沿自己的选择入口生成打造和分解限制装备 " + child.Core.ItemId);
            }
            string committed = State();
            SendOpen(client, 3, 1); client.Expect(1, 160, [0, 19, 0]); client.Barrier();
            That(State() == committed, "已耗普通父盒重放不再次发子盒或装备 " + sourceId);
        }
        return actors.ToArray();
        Character Read(ushort slot)
        {
            using var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { });
            return repo.LoadCharacter(owner, slot);
        }
        string State() => fixture.Snapshot() + fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") + fixture.Snapshot("SELECT * FROM sqlite_sequence ORDER BY name");
    }

    static void CharacterPool(TcpTestFixture fixture, TcpTestResources resources, CharacterRepository repo, AccountOwners owners)
    {
        const int sourceId = 490704573;
        var items = resources.ItemResources;
        var now = DateTimeOffset.UtcNow;
        var source = InventoryRewardGrantService.Create(items.Items.Get(sourceId),utcNow:now);
        var expired = InventoryRewardGrantService.Create(items.Items.Get(sourceId),utcNow:now.AddDays(-31));
        That(source.ExpireTime == now.AddDays(30).ToUnixTimeSeconds() && expired.IsExpired(now.ToUnixTimeSeconds()),
            "TCP代表角色的正常与过期源均由真实30天模板创建");
        Character created;
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount,() => { }))
            created = repo.Create(owner,new("CharactorTcp",resources.Towns.Room,resources.Towns.InitialReturn)
            { Job = 15, GrowType = 0x23, Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(15).Skills,
                Inventory = new([new(0,Bag(65),source),new(0,Bag(66),expired)],Gold:12345) });
        var pool = items.Boosters.Get(sourceId).Pools.Single(pool => pool.JobTag == ItemCatalog.JobTag(created.Job));
        var range = ItemSlotBoundService.Range(ItemKind.Emblem,created.Inventory);
        SetExpirationDebug(fixture,false);
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot);
            string before = State();
            fixture.Sql("CREATE TRIGGER fail_charactor_tcp BEFORE INSERT ON character_inventory_slots WHEN NEW.character_id="+created.Id+
                " AND NEW.list_type=0 AND NEW.slot_index="+range.Start+" BEGIN SELECT RAISE(ABORT,'charactor TCP rollback'); END");
            SendOpen(peer,65,1);
            peer.Closed();
            That(State() == before,"真实charactor奖励槽晚SQL失败不发成功ACK或delta，源、完整核心、槽、钱包、审计与UID序列回滚");
            fixture.Sql("DROP TRIGGER fail_charactor_tcp");
        }
        InventoryItem reward;
        byte[] rewardRow;
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot);
            var before = Read();
            That(before.Inventory.Find(Bag(65)) is { Uid: > 0, Core: var savedSource } && savedSource == source,
                "SQL故障重连后恢复同一个完整30天源");
            SendOpen(peer,65,1);
            byte[] ack = peer.Expect(1,160).Body;
            int id = ack.Length == 26 ? (int)U32(ack,18) : 0;
            That(pool.Rewards.Any(row => row.Weight > 0 && row.ItemId == id && row.Count == 1),
                "非零职业觉醒角色仅从本职业真实charactor池发奖");
            That(ack.SequenceEqual(Build(writer =>
            { writer.Write((byte)1); writer.Write((byte)0); writer.Write(sourceId); writer.Write((short)65);
                writer.Write(0); writer.Write(0); writer.Write((ushort)1); writer.Write(id); writer.Write(1); })),
                "普通6B CMD160沿18B成功头与8B单奖，完整ACK逐字节核对");
            var saved = Read();
            reward = saved.Inventory.Items.Single(item => item.Core.Kind == ItemKind.Emblem);
            var template = items.Items.Get(id);
            That(saved.Inventory.Find(Bag(65)) is null && saved.Revision == before.Revision+1 && reward.Uid > 0 &&
                reward.Position.List == InventoryListType.Main && reward.Position.Index >= range.Start && reward.Position.Index <= range.End &&
                reward.Core == InventoryRewardGrantService.Create(template,utcNow:now) && reward.Core.ExpireTime == 0 && saved.Inventory.Gold == 12345,
                "ACK与SQL唯一完整永久Emblem一致，源仅耗一次，存入正式Emblem页且获得正UID");
            // 该族新Emblem只有ID/数量/模板耐久与封装位；独立金样写其余109B为零，不调用生产Builder。
            rewardRow = Build(writer =>
            { writer.Write(reward.Position.Index); writer.Write(id); writer.Write(1); writer.Write((byte)0);
                writer.Write(template.Durability); writer.Write(template.AttachType.Contains("[sealing]",StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0);
                writer.Write(new byte[109]); });
            peer.Expect(0,14,Build(writer =>
            { writer.Write((byte)0); writer.Write((ushort)2); writer.Write((short)65); writer.Write(-1);
                writer.Write(new byte[117]); writer.Write(rewardRow); }));
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            byte[] full = peer.Select(created.Slot)[4].Body;
            int count = U16(full,3);
            That(full[0] == 0 && full.Length == 5+count*123,"重登NOTI13保留完整Main行宽");
            int at = Enumerable.Range(0,count).Select(index => 5+index*123).Single(index => U16(full,index) == reward.Position.Index);
            That(full[at..(at+123)].SequenceEqual(rewardRow) && Read().Inventory.Find(reward.Position) == reward &&
                !Enumerable.Range(0,count).Select(index => 5+index*123).Any(index => U16(full,index) == 65),
                "重登逐字节恢复永久奖励，SQL正UID和完整核心不变，已耗源不再投影");
            foreach (short slot in new short[] { 65,66 })
            {
                string before = State();
                SendOpen(peer,slot,1); peer.Expect(1,160,[0,19,0]); peer.Barrier();
                That(State() == before,"耗尽或已过期并可能被选角清理的源不再发奖，连接保持正常："+slot);
            }
        }
        Character Read()
        {
            using var owner = owners.Acquire(fixture.Options.DevelopmentAccount,() => { });
            return new CharacterRepository(fixture.Database).List(owner).Single(role => role.Id == created.Id);
        }
        string State() => fixture.Snapshot("SELECT * FROM characters ORDER BY id")+
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid")+
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index")+
            fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account")+
            fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY account,currency_kind")+
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id")+
            fixture.Snapshot("SELECT * FROM currency_audit_log ORDER BY audit_id")+
            fixture.Snapshot("SELECT * FROM sqlite_sequence ORDER BY name");
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
