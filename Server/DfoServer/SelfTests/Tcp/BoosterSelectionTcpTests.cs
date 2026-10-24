using System.Text.Json;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Management;
using static DfoServer.Testing.BoosterTcpTests;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class BoosterSelectionTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var itemResources = resources.ItemResources;
        LegacyExpiryFixture.Restore(itemResources.Items, (10002064, "2013-09-24 06:00:00"));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "booster-selection"), gm: true, resources: resources);
        var characters = resources.Characters;
        var towns = resources.Towns;
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            repo.Create(owner, new("SelectTcp", towns.Room, towns.InitialReturn)
            {
                Job = 15, AcceptedStoryLevel = 1, Skills = characters.Get(15).Skills,
                Inventory = new([new(0, Bag(3), Core(10000743, 2)), new(0, Bag(4), Core(10012332)),
                    new(0, Bag(5), Core(8403)), new(0, Bag(6), Core(10161354)), new(0, Bag(7), Core(10304530)),
                    new(0, Bag(8), Core(10012104)), new(0, Bag(65), Core(10000743)), new(0, Bag(67), Core(10002056))], Gold: 12345)
            });
            repo.Create(owner, new("EmptyDupTcp", towns.Room, towns.InitialReturn)
            {
                Job = 0, Level = 95, AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills,
                Inventory = new([new(0, Bag(65), Core(2749832))], Gold: 12345)
            });
        }
        int[] all = [100300855, 100312548, 100322438, 100344654, 100352921];
        int[] imported = [100050791, 100100692, 100150661];
        byte[][] importedEntries = [];
        await fixture.StartAsync();
        SetExpirationDebug(fixture, false);
        using (var peer = Open(fixture))
        {
            SendSelect(peer, 3, [10000871]);
            peer.Expect(1, 160, [0, 19, 0]);
            peer.Select();
            string before = Snapshot();
            foreach (var ids in new int[][] { [], [1106], [10000871, 10000871], [10000871, 10000872] })
            {
                SendSelect(peer, 3, ids);
                peer.Expect(1, 160, [0, 19, 0]);
            }
            SendSelect(peer, 3, [10000871], 1);
            peer.Expect(1, 160, [0, 19, 0]);
            peer.Send(160, [3, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]);
            peer.Barrier();
            That(Snapshot() == before, "bad choices and unsupported detail tail do not consume the source or report success");
            for (int i = 1; i <= 2; i++)
            {
                SendSelect(peer, 3, [10000871]);
                ExpectAck(peer, 10000743, 3, [(10000871, 1)]);
                byte[] delta = peer.Expect(0, 14).Body;
                Entry(delta, 3, i == 1 ? 10000743 : -1, i == 1 ? 1 : 0);
                Entry(delta, 66, 10000871, i);
            }
            SendSelect(peer, 65, [10000872]);
            ExpectAck(peer, 10000743, 65, [(10000872, 1)]);
            byte[] clear = peer.Expect(0, 14).Body;
            That(U16(clear, 1) == 1, "source-slot template replacement clears exactly the old source");
            Entry(clear, 65, -1, 0);
            Entry(peer.Expect(0, 14).Body, 65, 10000872, 1);
            SendSelect(peer, 4, [109000071]);
            ExpectAck(peer, 10012332, 4, [(109000071, 1)]);
            byte[] equipment = peer.Expect(0, 14).Body;
            Entry(equipment, 4, -1, 0);
            Entry(equipment, 9, 109000071, InventoryRewardGrantService.InitialQuality);
            SendSelect(peer, 5, [100332], 0, 0);
            peer.Expect(1, 160, [0, 19, 0]);
            SendSelect(peer, 5, [100332], 0, 1);
            ExpectAck(peer, 8403, 5, [(100332, 1)]);
            byte[] category = peer.Expect(0, 14).Body;
            Entry(category, 5, -1, 0);
            Entry(category, 10, 100332, InventoryRewardGrantService.InitialQuality);
            SendSelect(peer, 6, all[..4]);
            peer.Expect(1, 160, [0, 19, 0]);
            SendSelect(peer, 6, all.Reverse().ToArray());
            ExpectAck(peer, 10161354, 6, all.Select(id => (id, 1)).ToArray());
            byte[] whole = peer.Expect(0, 14).Body;
            That(U16(whole, 1) == 6, "whole group projects the source and all five outputs after ACK");
            Entry(whole, 6, -1, 0);
            for (int i = 0; i < all.Length; i++)
                Entry(whole, (short)(11 + i), all[i], InventoryRewardGrantService.InitialQuality);
            long earliestExpiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 14 * 86400;
            SendSelect(peer, 7, imported);
            ExpectAck(peer, 10304530, 7, imported.Select(id => (id, 1)).ToArray());
            byte[] temporary = peer.Expect(0, 14).Body;
            Entry(temporary, 7, -1, 0);
            importedEntries = imported.Select((id, index) =>
            {
                short slot = (short)(16 + index);
                Entry(temporary, slot, id, InventoryRewardGrantService.InitialQuality);
                byte[] entry = EquipmentEntry(temporary, slot);
                That(U16(entry, 11) == itemResources.Items.Get(id).Durability && entry[13] == 0 &&
                    U32(entry, 56) >= earliestExpiry && U32(entry, 56) <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 14 * 86400,
                    "selected imported equipment projects inherited durability and its own temporary expiry");
                return entry;
            }).ToArray();
            That(importedEntries.Select(entry => U32(entry, 56)).Distinct().Count() == 1,
                "one opening gives all imported rewards the same creation-time expiry");
            before = Snapshot();
            SendSelect(peer, 8, [10000219]);
            peer.Expect(1, 160, [0, 19, 0]);
            SendSelect(peer, 67, [10002064]);
            peer.Expect(1, 160, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == before, "default hints do not authorize missing candidates or expired selected rewards");
            SendSelect(peer, 8, [101000007]);
            ExpectAck(peer, 10012104, 8, [(101000007, 1)]);
            byte[] defaultEquipment = peer.Expect(0, 14).Body;
            That(U16(defaultEquipment, 1) == 2, "stale default causes neither an extra reward nor an extra projection");
            Entry(defaultEquipment, 8, -1, 0);
            Entry(defaultEquipment, 19, 101000007, InventoryRewardGrantService.InitialQuality);
            SendSelect(peer, 67, [3290, 3311]);
            peer.Expect(1, 160, [0, 19, 0]);
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "level SelectTcp 75", out var levelReply) &&
                levelReply.StartsWith("OK "), "通过原GM入口满足觉醒之息源盒真实最低75级，Lv1完整选择仍拒绝");
            SendSelect(peer, 67, [3290, 3311]);
            ExpectAck(peer, 10002056, 67, [(3290, 88), (3311, 88)]);
            byte[] defaultMaterials = peer.Expect(0, 14).Body;
            That(U16(defaultMaterials, 1) == 3, "defaults use real row quantities without automatically granting unchosen entries");
            Entry(defaultMaterials, 67, -1, 0);
            Entry(defaultMaterials, 121, 3290, 88);
            Entry(defaultMaterials, 122, 3311, 88);
            before = Snapshot();
            SendSelect(peer, 6, all);
            peer.Expect(1, 160, [0, 19, 0]);
            peer.Barrier();
            That(before == Snapshot(), "replay of consumed selection cannot grant again");
            That(fixture.Query("SELECT count(*),count(DISTINCT item_uid) FROM character_inventory_items") is [[16L, 16L]],
                "four stackable and eleven complete equipment instances persist once");
        }
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repo.List(owner).Single(character => character.Name == "SelectTcp");
            That(current.Inventory.Find(Bag(9))!.Core == Core(109000071), "full equipment core persists beyond the network projection");
            That(current.Inventory.Find(Bag(19))!.Core == Core(101000007) &&
                current.Inventory.Find(Bag(121))!.Core == Core(3290, 88) && current.Inventory.Find(Bag(122))!.Core == Core(3311, 88),
                "default selections persist only the selected complete cores and declared quantities");
            for (int i = 0; i < imported.Length; i++)
                That(current.Inventory.Find(Bag((short)(16 + i)))!.Core ==
                    Core(imported[i]) with { ExpireTime = checked((int)U32(importedEntries[i], 56)) },
                    "repository reopen retains each imported reward's complete core and original expiry");
            InventoryService.Commit(repo, owner, current, InventoryRewardGrantService.TryGrantBatch(current, [new(10161354, 1)], itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces));
        }
        using (var peer = Open(fixture))
        {
            byte[] full = peer.Select()[4].Body;
            Entry(full, 65, 10000872, 1, true);
            Entry(full, 66, 10000871, 2, true);
            Entry(full, 67, 10161354, 1, true);
            Entry(full, 9, 109000071, InventoryRewardGrantService.InitialQuality, true);
            Entry(full, 19, 101000007, InventoryRewardGrantService.InitialQuality, true);
            Entry(full, 121, 3290, 88, true);
            Entry(full, 122, 3311, 88, true);
            for (int i = 0; i < imported.Length; i++)
                That(EquipmentEntry(full, (short)(16 + i), true).SequenceEqual(importedEntries[i]),
                    "reconnect restores imported equipment without resetting its expiry or duplicating a reward");
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_selection BEFORE INSERT ON character_inventory_slots WHEN NEW.slot_index=21 BEGIN SELECT RAISE(ABORT,'selection rollback'); END");
            SendSelect(peer, 67, all);
            peer.Closed();
            That(before == Snapshot(), "later reward failure sends no success and rolls back source, earlier reward, revisions, audit and UID sequence");
            fixture.Sql("DROP TRIGGER fail_selection");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            SendSelect(peer, 67, all);
            ExpectAck(peer, 10161354, 67, all.Select(id => (id, 1)).ToArray());
            byte[] delta = peer.Expect(0, 14).Body;
            Entry(delta, 67, -1, 0);
            for (int i = 0; i < all.Length; i++)
                Entry(delta, (short)(20 + i), all[i], InventoryRewardGrantService.InitialQuality);
            peer.Barrier();
        }
        // 同一fixture复用正式资源，单独角色验证空重复源的落库、晚SQL回滚及重连。
        var group = itemResources.Boosters.Get(2749832).Selection!.Categories.First(category => !category.Rewards.IsEmpty);
        var choice = group.Rewards[0];
        var template = itemResources.Items.Get(choice.ItemId);
        That(template.Kind == DfoServer.GameWorld.Items.ItemKind.Equipment && choice.Count == 1 && choice.UpgradeChances.IsEmpty,
            "代表TCP选择固定的一件真实普通装备，不替换资源或随机结果");
        var expectedCore = new ItemCore(template.Id, template.Kind, 999_999_998)
        {
            Attr = choice.Upgrade, GenuineUpgrade = choice.Refinement, Durability = template.Durability,
            SealFlag = template.AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0,
            ExpireTime = template.Expiration.Resolve(DateTimeOffset.UtcNow)
        };
        That(expectedCore.ExpireTime == 0, "代表TCP奖励必须是真实永久模板，不能忽略期限");
        long parentUid = Convert.ToInt64(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=(SELECT id FROM characters WHERE name='EmptyDupTcp') AND slot_index=65")[0][0]);
        using (var peer = Open(fixture))
        {
            peer.Select(1);
            string before = Snapshot();
            SendSelect(peer, 65, [], 7, 2); peer.Expect(1, 160, [0, 19, 0]); peer.Barrier();
            That(Snapshot() == before, "真实空重复坐标仍拒绝且两个角色资产不变");
            fixture.Sql("CREATE TRIGGER fail_empty_duplicate BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'empty duplicate late rollback'); END");
            SendSelect(peer, 65, [choice.ItemId], group.Category, group.Subcategory); peer.Closed();
            That(Snapshot() == before, "空重复源已写物品和槽后审计故障无ACK并回滚全部已核资产、revision及序列");
            fixture.Sql("DROP TRIGGER fail_empty_duplicate");
        }
        byte[] savedEntry;
        using (var peer = Open(fixture))
        {
            var initial = peer.Select(1)[4].Body; Entry(initial, 65, 2749832, 1, true);
            SendSelect(peer, 65, [choice.ItemId], group.Category, group.Subcategory);
            ExpectAck(peer, 2749832, 65, [(choice.ItemId, 1)]);
            var delta = peer.Expect(0, 14).Body;
            That(U16(delta, 1) == 2, "新兼容源只投影清源和一件装备");
            Entry(delta, 65, -1, 0); Entry(delta, 9, choice.ItemId, 999_999_998);
            savedEntry = EquipmentEntry(delta, 9); peer.Barrier();
            string saved = Snapshot();
            SendSelect(peer, 65, [choice.ItemId], group.Category, group.Subcategory);
            peer.Expect(1, 160, [0, 19, 0]); peer.Barrier();
            That(Snapshot() == saved, "新兼容源耗尽重放不重复发奖");
        }
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var saved = repo.List(owner).Single(character => character.Name == "EmptyDupTcp");
            That(saved.Inventory.Items is [{ Position.Index: 9 } reward] && reward.Uid != parentUid && reward.Core == expectedCore &&
                saved.Inventory.Gold == 12345 && fixture.Query("SELECT item_uid FROM character_inventory_items WHERE item_uid=$p0", parentUid).Length == 0,
                "新兼容源UID注销且完整装备核心和数量只持久化一次");
        }
        using (var peer = Open(fixture))
        {
            var full = peer.Select(1)[4].Body;
            That(EquipmentEntry(full, 9, true).SequenceEqual(savedEntry), "重连恢复新兼容盒完整123B装备且不重建期限或核心");
            peer.Barrier();
        }
        RunProgressiveCategories(fixture, resources, 2);
        await fixture.StopAsync();
        That(File.ReadLines(Path.Combine(fixture.Options.LogDirectory, "server.jsonl")).Count(line =>
        {
            using var entry = JsonDocument.Parse(line); var data = entry.RootElement;
            return data.GetProperty("Kind").GetString() == "closed" &&
                data.GetProperty("Detail").GetProperty("Reason").GetString() == "SqliteException" &&
                data.GetProperty("Detail").GetProperty("Message").GetString() == "SQLite Error 19: 'empty duplicate late rollback'.";
        }) == 1, "新兼容盒断线精确来自所注入的晚SQL故障一次");
        That(fixture.Query("SELECT gold FROM characters ORDER BY slot") is [[12345L], [12345L], [12345L], [12345L]], "selection does not add a gold or purchase-material fee");
        That(File.ReadLines(Path.Combine(fixture.Options.LogDirectory, "server.jsonl")).Count(line =>
        {
            using var entry = JsonDocument.Parse(line); var data = entry.RootElement;
            return data.GetProperty("Kind").GetString() == "closed" &&
                data.GetProperty("Detail").GetProperty("Reason").GetString() == "SqliteException" &&
                data.GetProperty("Detail").GetProperty("Message").GetString() == "SQLite Error 19: 'progressive category late rollback'.";
        }) == 1, "逐项扩展盒断线精确来自晚SQL故障一次");
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0, "selection database integrity");
        await RunRealTemplateSelectionsAsync(source, root, resources);
        await RunOrdinaryNativeSelectionsAsync(source, root, resources);

        ItemCore Core(int id, int count = 1) => InventoryRewardGrantService.Create(itemResources.Items.Get(id), count);
        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT revision,gold FROM characters")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT list_type,slot_index,item_uid FROM character_inventory_slots ORDER BY slot_index")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'"));
    }
    static void RunProgressiveCategories(TcpTestFixture fixture, TcpTestResources resources, ushort selectedSlot)
    {
        const int sourceId = 490001689;
        ushort guardSlot = checked((ushort)(selectedSlot + 1));
        var items = resources.ItemResources.Items; var repo = new CharacterRepository(fixture.Database); var owners = new AccountOwners();
        var now = DateTimeOffset.UtcNow;
        That(items.Get(sourceId).StackLimit == 1 && items.Get(sourceId).Expiration.Resolve(now) == 0 && items.Get(3242).StackLimit >= 37,
            "逐项扩展礼盒真实堆叠上限为1，哨兵数量37在真实模板上限内");
        InventoryItem Seed(short slot, int id, int count = 1) => new(0, Bag(slot), InventoryRewardGrantService.Create(items.Get(id), count, now));
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            repo.Create(owner, new("ProgSelTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = 0, Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([Seed(65, sourceId), Seed(66, sourceId), Seed(121, 3242, 37)], Gold: 12345)
            });
            repo.Create(owner, new("ProgFullTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = 0, Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new(Enumerable.Range(9, 56).Select(slot => Seed((short)slot, 405044)).Append(Seed(65, sourceId)).ToImmutableArray(), Gold: 12345)
            });
        }
        DfoServer.Game.Characters.Character Read(ushort slot)
        {
            using var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }); return repo.LoadCharacter(owner, slot);
        }
        SortedDictionary<string, string[]> SqlState()
        {
            var state = new SortedDictionary<string, string[]>();
            foreach (var row in fixture.Query("SELECT name FROM sqlite_schema WHERE type='table' ORDER BY name"))
            {
                string name = (string)row[0]!;
                state[name] = fixture.Query("SELECT * FROM \"" + name.Replace("\"", "\"\"") + "\"")
                    .Select(values => JsonSerializer.Serialize(values)).Order(StringComparer.Ordinal).ToArray();
            }
            return state;
        }
        string Snapshot() => JsonSerializer.Serialize(SqlState());
        string OtherAssets(ushort id) => JsonSerializer.Serialize(fixture.Query("SELECT * FROM characters WHERE id<>$p0 ORDER BY id", id)) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_items WHERE character_id<>$p0 ORDER BY item_uid", id)) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_slots WHERE character_id<>$p0 ORDER BY character_id,list_type,slot_index", id));
        ItemCore ExpectedCore(int id)
        {
            var template = items.Get(id);
            That(template.Kind == ItemKind.Equipment && template.Expiration.Resolve(now) == 0, "(6,0)代表奖励是真实永久普通装备");
            return new(id, ItemKind.Equipment, 999_999_998)
            {
                Durability = template.Durability,
                SealFlag = template.AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0
            };
        }
        using (var peer = Open(fixture))
        {
            peer.Select(guardSlot); string before = Snapshot();
            SendSelect(peer, 65, [405044], 6); peer.Expect(1, 160, [0, 4, 0]); peer.Barrier();
            That(Snapshot() == before, "真实满装备页拒绝后定义分类，不扣盒且所有SQL表与序列不变");
        }
        Dictionary<short, long> sourceUids;
        using (var peer = Open(fixture))
        {
            peer.Select(selectedSlot); var beforeCharacter = Read(selectedSlot);
            sourceUids = new short[] { 65, 66 }.ToDictionary(slot => slot, slot => beforeCharacter.Inventory.Find(Bag(slot))!.Uid);
            That(sourceUids.Values.All(uid => uid > 0) && sourceUids.Values.Distinct().Count() == 2 && sourceUids.Keys.All(slot =>
                beforeCharacter.Inventory.Find(Bag(slot))!.Core is { ItemId: sourceId, Value: 1, ExpireTime: 0 }),
                "逐项扩展TCP源为两份不同正UID、各一只的真实未过期礼盒");
            string before = Snapshot();
            foreach (var (category, subcategory, ids) in new (byte, byte, int[])[] {
                (10, 0, [405044]), (6, 4, [405044]), (6, 0, [405002]), (6, 0, [1106]),
                (6, 0, []), (6, 0, [405044, 405044]), (6, 0, [405044, 405042]) })
            {
                SendSelect(peer, 65, ids, category, subcategory); peer.Expect(1, 160, [0, 19, 0]);
            }
            peer.Barrier(); That(Snapshot() == before, "非法坐标、非本分类ID、重复和超额选择不改变任何SQL资产");
            fixture.Sql("CREATE TRIGGER fail_progressive_category BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'progressive category late rollback'); END");
            SendSelect(peer, 65, [405044], 6); peer.Closed();
            That(Snapshot() == before && JsonSerializer.Serialize(Read(selectedSlot)) == JsonSerializer.Serialize(beforeCharacter),
                "后定义分类在源与奖励已写入后的晚审计失败无ACK，整批回滚完整SQL/UID/钱包/revision");
            fixture.Sql("DROP TRIGGER fail_progressive_category");
        }
        var entries = new Dictionary<short, byte[]>();
        foreach (var (sourceSlot, rewardId, slot) in new[] { ((short)65, 405044, (short)9), ((short)66, 415043, (short)10) })
        {
            using var peer = Open(fixture); var full = peer.Select(selectedSlot)[4].Body;
            foreach (var saved in entries) That(EquipmentEntry(full, saved.Key, true).SequenceEqual(saved.Value), "再次连接恢复先前装备完整123B");
            var before = Read(selectedSlot); var guard = Read(guardSlot); var sqlBefore = SqlState();
            string otherAssets = OtherAssets(before.Id);
            var sequences = fixture.Query("SELECT name,seq FROM sqlite_sequence").ToDictionary(row => (string)row[0]!, row => Convert.ToInt64(row[1]));
            var source = before.Inventory.Find(Bag(sourceSlot))!;
            That(source.Uid == sourceUids[sourceSlot] && source.Core.Value == 1, "成功请求仅消费对应槽位的一只原始正UID礼盒");
            long nextUid = Convert.ToInt64(fixture.Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'")[0][0]) + 1;
            var expectedItems = before.Inventory.Items.Where(item => item.Uid != source.Uid).ToList();
            var expectedCore = ExpectedCore(rewardId); expectedItems.Add(new(nextUid, Bag(slot), expectedCore));
            var expected = before with { Revision = before.Revision + 1, Inventory = before.Inventory with {
                Items = expectedItems.OrderBy(item => item.Position.List).ThenBy(item => item.Position.Index).ToImmutableArray() } };
            SendSelect(peer, sourceSlot, [rewardId], 6); ExpectAck(peer, sourceId, sourceSlot, [(rewardId, 1)]);
            var delta = peer.Expect(0, 14).Body; That(U16(delta, 1) == 2, "后定义成功只投影源与一件真实装备");
            Entry(delta, sourceSlot, -1, 0); Entry(delta, slot, rewardId, 999_999_998);
            entries.Add(slot, EquipmentEntry(delta, slot)); peer.Barrier();
            That(JsonSerializer.Serialize(Read(selectedSlot)) == JsonSerializer.Serialize(expected) && JsonSerializer.Serialize(Read(guardSlot)) == JsonSerializer.Serialize(guard),
                "真实后定义发奖完整角色/Core/UID/源数量/钱包与满包观察角色符合独立预期");
            var sqlAfter = SqlState(); string[] changed = ["characters", "character_inventory_items", "character_inventory_slots", "inventory_audit_log", "sqlite_sequence"];
            That(sqlBefore.Keys.SequenceEqual(sqlAfter.Keys) && sqlBefore.Where(table => !changed.Contains(table.Key)).All(table => table.Value.SequenceEqual(sqlAfter[table.Key])),
                "成功只改变既有背包事务表，其他钱包、邮件、技能及全部SQL表不变");
            That(OtherAssets(before.Id) == otherAssets && fixture.Query("SELECT name,seq FROM sqlite_sequence").All(row =>
                Convert.ToInt64(row[1]) == sequences.GetValueOrDefault((string)row[0]!) + ((string)row[0]! == "character_inventory_items" ? 1 : (string)row[0]! == "inventory_audit_log" ? 2 : 0)),
                "成功保留所有旁观角色完整SQL/实例，序列只增加一件装备和两条审计");
            That(sqlAfter["inventory_audit_log"].Length == sqlBefore["inventory_audit_log"].Length + 2 &&
                sqlBefore["inventory_audit_log"].All(row => sqlAfter["inventory_audit_log"].Contains(row)) &&
                fixture.Query("SELECT item_uid FROM character_inventory_items WHERE item_uid=$p0", source.Uid).Length == 0 &&
                fixture.Query("SELECT item_uid FROM character_inventory_items WHERE item_uid=$p0", nextUid).Length == 1 &&
                fixture.Query("SELECT count(*)-count(DISTINCT item_uid) FROM character_inventory_items") is [[0L]], "每次原源UID注销、源和奖励各一条审计，新UID只持久化一次");
        }
        using (var peer = Open(fixture))
        {
            var full = peer.Select(selectedSlot)[4].Body;
            foreach (var saved in entries) That(EquipmentEntry(full, saved.Key, true).SequenceEqual(saved.Value), "保存重连恢复两件后定义装备完整123B");
            string before = Snapshot();
            foreach (short sourceSlot in sourceUids.Keys) { SendSelect(peer, sourceSlot, [405044], 6); peer.Expect(1, 160, [0, 19, 0]); }
            peer.Barrier();
            That(Snapshot() == before && sourceUids.All(source => Read(selectedSlot).Inventory.Find(Bag(source.Key)) is null &&
                fixture.Query("SELECT item_uid FROM character_inventory_items WHERE item_uid=$p0", source.Value).Length == 0),
                "两份耗尽源UID均注销，两源槽重复请求不重复发奖或改变全部SQL");
        }
    }
    static async Task RunRealTemplateSelectionsAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int[] sourceIds = [10007539, 10007726, 10007992, 10007993, 10007996, 490700181,
            490700184, 490700980, 490701127, 490701129, 490702047];
        var items = resources.ItemResources.Items;
        var definitions = sourceIds.Select(id => (Id: id, Category: resources.ItemResources.Boosters.Get(id).Selection!.Categories.Single())).ToArray();
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "real-template-selection"), gm: true, resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repo.Create(owner, new("RealSel", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = 15, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(15).Skills,
                Inventory = new(definitions.Select((entry, index) => new InventoryItem(0, Bag((short)(65 + index)),
                    InventoryRewardGrantService.Create(items.Get(entry.Id), entry.Category.Rewards.Length, shopExpiration: true))).ToImmutableArray())
            });
        await fixture.StartAsync();
        SetExpirationDebug(fixture, true);
        using (var peer = Open(fixture))
        {
            using var readOwner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { });
            peer.Select();
            foreach (var (id, category) in definitions)
            {
                short slot = (short)(65 + Array.FindIndex(sourceIds, sourceId => sourceId == id));
                foreach (var choice in category.Rewards)
                {
                    SendSelect(peer, slot, [choice.ItemId], category.Category, category.Subcategory);
                    ExpectAck(peer, id, slot, [(choice.ItemId, choice.Count)]);
                    var template = items.Get(choice.ItemId);
                    byte[] changed = peer.Expect(0, 14).Body;
                    byte[] sourceChanged = changed;
                    if (template.Kind == ItemKind.Avatar)
                        changed = peer.Expect(0, 14).Body;
                    var committed = repo.List(readOwner).Single();
                    var reward = committed.Inventory.Items.Single(item => item.Core.ItemId == choice.ItemId);
                    That(changed.SequenceEqual(InventoryPackets.Delta(committed.Inventory, reward.Position.List,
                            [Bag(slot), reward.Position])) &&
                        (template.Kind != ItemKind.Avatar || sourceChanged.SequenceEqual(
                            InventoryPackets.Delta(committed.Inventory, InventoryListType.Main, [Bag(slot)]))),
                        "CMD160成功通知逐字匹配已提交实例的正式投影 " + choice.ItemId);
                    int width = template.Kind == ItemKind.Avatar ? 165 : 123;
                    int count = U16(changed, 1);
                    That(changed[0] == (template.Kind == ItemKind.Avatar ? 1 : 0) && changed.Length == 3 + width * count &&
                        Enumerable.Range(0, count).Any(index => U32(changed, 3 + index * width + 2) == choice.ItemId),
                        "CMD160已提交真实候选的对应容器完整通知 " + id + ":" + choice.ItemId);
                    peer.Barrier();
                }
            }
            string before = JsonSerializer.Serialize(fixture.Query("SELECT revision FROM characters")) +
                JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid"));
            SendSelect(peer, 65, [definitions[0].Category.Rewards[0].ItemId]);
            peer.Expect(1, 160, [0, 19, 0]);
            peer.Barrier();
            That(before == JsonSerializer.Serialize(fixture.Query("SELECT revision FROM characters")) +
                JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")),
                "源耗尽后再次提交选择不会额外发奖或推进版本");
        }
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var saved = repo.List(owner).Single();
            var selectedIds = definitions.SelectMany(entry => entry.Category.Rewards.Select(row => row.ItemId)).ToArray();
            That(saved.Inventory.Items.Length == 33 && saved.Inventory.Items.Select(item => item.Core.ItemId).Order().SequenceEqual(selectedIds.Order()),
                "11盒33项均完整扣源并在数据库中各保存一个真实模板实例");
            foreach (var reward in saved.Inventory.Items)
            {
                var template = items.Get(reward.Core.ItemId);
                var expected = InventoryRewardGrantService.CreateItem(template, reward.Position,
                    InventoryRewardGrantService.Create(template, utcNow: DateTimeOffset.UtcNow, shopExpiration: true));
                if (template.Kind == ItemKind.Avatar)
                    expected = expected with { Core = expected.Core with { Value = checked((int)reward.Uid) } };
                That(reward == expected with { Uid = reward.Uid },
                    "保存重读的完整核心、耐久、封装、品质、期限与全部详情等于真实模板默认实例 " + reward.Core.ItemId);
                That(reward.Core.Kind == template.Kind && reward.Core.Count == 1 && reward.Uid > 0 &&
                    (template.Kind == ItemKind.Avatar
                        ? reward.Position.List == InventoryListType.Avatar && reward.Avatar is not null &&
                            reward.Core.Durability == 0 &&
                            reward.Avatar.Socket0 == AvatarSocket.Empty(template.AvatarSockets.First) &&
                            reward.Avatar.Socket1 == AvatarSocket.Empty(template.AvatarSockets.Second) &&
                            reward.Avatar.Socket2 == AvatarSocket.Empty(template.AvatarSockets.Third) &&
                            reward.Avatar.Socket3 == AvatarSocket.Empty(template.AvatarSockets.Fourth) &&
                            reward.Avatar.Socket4 == AvatarSocket.Empty(template.AvatarSockets.Fifth)
                        : reward.Position.List == InventoryListType.Main && reward.Avatar is null),
                    "保存重读保留真实Kind、UID、容器和模板孔位详情 " + reward.Core.ItemId);
            }
        }
        using (var peer = Open(fixture))
        {
            byte[] full = peer.Select()[4].Body;
            int[] titles = definitions.SelectMany(entry => entry.Category.Rewards.Select(row => row.ItemId))
                .Where(id => items.Get(id).Kind == ItemKind.Equipment).Order().ToArray();
            int fullCount = U16(full, 3);
            int[] fullIds = Enumerable.Range(0, fullCount).Select(index => (int)U32(full, 5 + index * 123 + 2)).Order().ToArray();
            That(full.Length == 5 + fullCount * 123 &&
                titles.All(id => fullIds.Count(actual => actual == id) == 1),
                "重连完整123B装备表中各恢复一件21项称号");
            byte[] avatars = peer.SpecialContainers[1].Body;
            int[] auras = definitions.SelectMany(entry => entry.Category.Rewards.Select(row => row.ItemId))
                .Where(id => items.Get(id).Kind == ItemKind.Avatar).Order().ToArray();
            That(avatars[0] == 1 && U16(avatars, 3) == auras.Length && avatars.Length == 5 + auras.Length * 165 &&
                Enumerable.Range(0, auras.Length).Select(index => (int)U32(avatars, 5 + index * 165 + 2)).Order().SequenceEqual(auras),
                "重连恢复12件光环的完整165B条目与真实ID");
            peer.Barrier();
        }
        await fixture.StopAsync();
        That(fixture.Query("SELECT count(*) FROM inventory_avatar_details") is [[12L]] &&
            fixture.Query("SELECT count(*),count(DISTINCT item_uid) FROM character_inventory_items") is [[33L, 33L]] &&
            fixture.Query("PRAGMA integrity_check") is [["ok"]], "33件真实选择奖励及12条光环详情一次持久化");
    }
    static async Task RunOrdinaryNativeSelectionsAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var items = resources.ItemResources.Items;
        int[] sources = [10007740, 10007999];
        var categories = sources.Select(id => resources.ItemResources.Boosters.Get(id).Selection!.Categories.Single()).ToArray();
        var now = DateTimeOffset.UtcNow;
        var sourceRoles = sources.Select(id => Inventory.BoosterNativeSelectionTests.ResourceRoles(resources.Towns, resources.Characters, items.Get(id), now).ToArray()).ToArray();
        var legal = sourceRoles[0].First(role => sourceRoles[1].Any(other => role.Job == other.Job));
        byte level = sourceRoles.Max(roles => roles.Single(role => role.Job == legal.Job).Level);
        var initial = legal with { Level = level, Inventory = new(sources.Select((id, index) => new InventoryItem(0,
            Bag((short)(65 + index)), InventoryRewardGrantService.Create(items.Get(id), categories[index].Rewards.Length, now))).ToImmutableArray()) };
        That(initial.Inventory.Items.All(item => InventoryAvatarPackageService.SourceEligible(initial, item, items.Get(item.Core.ItemId))),
            "普通类型TCP角色按两源实际职业、等级、技能资格创建");
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "ordinary-native-selection"), gm: true, resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repo.Create(owner, new("OrdinarySel", initial.Position, initial.Return)
            { Job = initial.Job, Level = initial.Level, Skills = initial.Skills, AcceptedStoryLevel = 1, Inventory = initial.Inventory });
        await fixture.StartAsync();
        SetExpirationDebug(fixture, false);
        // 两种新Kind分别触发真实SQL写入失败，源扣减、UID分配、revision和审计必须同回滚。
        foreach (int index in new[] { 0, 1 })
        {
            using var peer = Open(fixture);
            peer.Select();
            string before = Snapshot();
            int list = index == 0 ? 0 : 7, slot = index == 0 ? 67 : 140;
            fixture.Sql($"CREATE TRIGGER fail_native_selection BEFORE INSERT ON character_inventory_slots WHEN NEW.list_type={list} AND NEW.slot_index={slot} BEGIN SELECT RAISE(ABORT,'native selection rollback'); END");
            SendSelect(peer, (short)(65 + index), [categories[index].Rewards[0].ItemId], categories[index].Category, categories[index].Subcategory);
            peer.Closed();
            That(before == Snapshot(), "原生普通选择写入失败整笔回滚且没有成功响应 " + sources[index]);
            fixture.Sql("DROP TRIGGER fail_native_selection");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            using var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { });
            foreach (int index in new[] { 0, 1 })
                foreach (var choice in categories[index].Rewards)
                {
                    var category = categories[index];
                    short sourceSlot = (short)(65 + index);
                    SendSelect(peer, sourceSlot, [choice.ItemId], category.Category, category.Subcategory);
                    ExpectAck(peer, sources[index], sourceSlot, [(choice.ItemId, choice.Count)]);
                    var saved = repo.List(owner).Single();
                    var reward = saved.Inventory.Items.Single(item => item.Core.ItemId == choice.ItemId);
                    var template = items.Get(choice.ItemId);
                    var expectedCore = new ItemCore(choice.ItemId, index == 0 ? ItemKind.Consumable : ItemKind.CreatureEquipment,
                        index == 0 ? choice.Count : 999_999_998)
                    { Durability = template.Durability, SealFlag = template.AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0 };
                    That(template.Expiration == new ItemExpiration() && reward.Core == expectedCore && reward.Uid > 0 &&
                        reward.Avatar is null && reward.Creature is null &&
                        (index == 0 ? reward.Position.List == InventoryListType.Main && reward.Position.Index is >= 65 and <= 120 :
                            reward.Position.List == InventoryListType.Pet && reward.Position.Index is >= 140 and <= 188),
                        "真实六候选持久化核心字面预期含Kind、质量、期限、页和无额外详情 " + choice.ItemId);
                    if (reward.Position == Bag(sourceSlot))
                        That(peer.Expect(0, 14).Body.SequenceEqual(InventoryPackets.Delta(InventorySnapshot.Empty,
                            InventoryListType.Main, [Bag(sourceSlot)])), "耗尽源格被宝珠复用时先清槽再同步新完整核心");
                    byte[] main = peer.Expect(0, 14).Body;
                    That(main.SequenceEqual(InventoryPackets.Delta(saved.Inventory, InventoryListType.Main, [Bag(sourceSlot), reward.Position])),
                        "普通选择ACK后的Main通知精确匹配已提交源及奖励");
                    if (index == 1)
                        That(peer.Expect(0, 14).Body.SequenceEqual(InventoryPackets.Delta(saved.Inventory, InventoryListType.Pet, [reward.Position])),
                            "宠物装备完整123B通知匹配已提交Pet实例");
                    peer.Barrier();
                }
            foreach (int index in new[] { 0, 1 })
            {
                string before = Snapshot();
                SendSelect(peer, (short)(65 + index), [categories[index].Rewards[0].ItemId], categories[index].Category, categories[index].Subcategory);
                peer.Expect(1, 160, [0, 19, 0]);
                peer.Barrier();
                That(before == Snapshot(), "两种普通类型源耗尽重放不增加资产、UID或审计");
            }
        }
        using (var peer = Open(fixture))
        {
            byte[] main = peer.Select()[4].Body;
            using var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { });
            var saved = repo.List(owner).Single();
            That(saved.Inventory.Items.Length == 6 && main.SequenceEqual(InventoryPackets.Full(saved.Inventory)) &&
                peer.SpecialContainers[2].Body.SequenceEqual(InventoryPackets.SpecialFull(saved.Inventory, InventoryListType.Pet)),
                "重连完整Main和Pet表恢复全部三宝珠及三宠物装备");
            peer.Barrier();
        }
        await fixture.StopAsync();
        That(fixture.Query("SELECT count(*),count(DISTINCT item_uid) FROM character_inventory_items") is [[6L, 6L]] &&
            fixture.Query("SELECT count(*) FROM inventory_avatar_details") is [[0L]] &&
            fixture.Query("SELECT count(*) FROM inventory_creature_details") is [[0L]] &&
            fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "两种新Kind只落六个实体、不产生Avatar或Creature详情且数据库完整");

        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT * FROM characters")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT list_type,slot_index,item_uid FROM character_inventory_slots ORDER BY list_type,slot_index")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'"));
    }

    internal static byte[] EquipmentEntry(byte[] body, short slot, bool full = false)
    {
        int header = full ? 5 : 3;
        int at = Enumerable.Range(0, U16(body, full ? 3 : 1)).Select(index => header + index * 123).Single(at => U16(body, at) == slot);
        return body.AsSpan(at, 123).ToArray();
    }
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    internal static void SendSelect(TcpPeer peer, short slot, int[] ids, byte category = 0, byte subcategory = 0, int count = 1) => peer.Send(160, Build(writer =>
    {
        writer.Write(slot);
        writer.Write(count);
        writer.Write(category);
        writer.Write(subcategory);
        foreach (int id in ids)
            writer.Write(id);
        writer.Write((byte)0);
        writer.Write((byte)0);
    }));
}
