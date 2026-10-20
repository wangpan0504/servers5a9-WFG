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
            That(fixture.Query("SELECT count(*),count(DISTINCT item_uid) FROM character_inventory_items") is [[15L, 15L]],
                "four stackable and eleven complete equipment instances persist once");
        }
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repo.List(owner).Single();
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
        await fixture.StopAsync();
        That(fixture.Query("SELECT gold FROM characters") is [[12345L]], "selection does not add a gold or purchase-material fee");
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0, "selection database integrity");
        await RunRealTemplateSelectionsAsync(source, root, resources);

        ItemCore Core(int id, int count = 1) => InventoryRewardGrantService.Create(itemResources.Items.Get(id), count);
        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT revision,gold FROM characters")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT list_type,slot_index,item_uid FROM character_inventory_slots ORDER BY slot_index")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'"));
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
