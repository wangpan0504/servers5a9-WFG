using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using DfoServer.Network.Management;
using static DfoServer.Testing.BoosterSelectionTcpTests;
using static DfoServer.Testing.BoosterTcpTests;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class BoosterEquipmentTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var itemResources = resources.ItemResources;
        Inventory.BoosterEquipmentTests.Run(resources.Towns, resources.Characters, itemResources);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "booster-equipment"), gm: true, resources: resources);
        var characters = resources.Characters;
        var towns = resources.Towns;
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            repo.Create(owner, new("EquipmentTcp", towns.Room, towns.InitialReturn)
            {
                Job = 15, AcceptedStoryLevel = 1, Skills = characters.Get(15).Skills,
                Inventory = new([new(0, Bag(3), Core(10005273)), new(0, Bag(4), Core(10014751, 2)),
                    new(0, Bag(5), Core(10014751, 2)), new(0, Bag(6), Core(10005273)), new(0, Bag(7), Core(10005273))], Gold: 12345)
            });
        }
        var committedEntries = new List<byte[]>();
        await fixture.StartAsync();
        SetExpirationDebug(fixture, false);
        using (var peer = Open(fixture))
        {
            peer.Select();
            long earliestExpiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60 * 86400;
            SendSelect(peer, 3, [401000005]);
            ExpectAck(peer, 10005273, 3, [(401000005, 1)]);
            byte[] delta = peer.Expect(0, 14).Body;
            Entry(delta, 3, -1, 0);
            Entry(delta, 9, 401000005, InventoryRewardGrantService.InitialQuality);
            byte[] weapon = EquipmentEntry(delta, 9);
            That(weapon[10] == 7 && weapon[87] == 3 && weapon[18] == 0 && U32(weapon, 14) == 0 &&
                U16(weapon, 11) == itemResources.Items.Get(401000005).Durability &&
                U32(weapon, 56) >= earliestExpiry && U32(weapon, 56) <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60 * 86400,
                "fixed reinforcement and refinement occupy the verified raw fields without changing template or expiry");
            committedEntries.Add(weapon);
            SendSelect(peer, 4, [16000], count: 2);
            ExpectAck(peer, 10014751, 4, [(16000, 2)]);
            delta = peer.Expect(0, 14).Body;
            Entry(delta, 4, -1, 0);
            CheckRandom(delta, 10);
            CheckRandom(delta, 11);
            peer.Barrier();
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_equipment_reward BEFORE INSERT ON character_inventory_slots WHEN NEW.slot_index=13 BEGIN SELECT RAISE(ABORT,'equipment reward rollback'); END");
            SendSelect(peer, 5, [16000], count: 2);
            peer.Closed();
            That(Snapshot() == before, "later reward failure closes without success and atomically restores all source and reward state");
            fixture.Sql("DROP TRIGGER fail_equipment_reward");
        }
        using (var peer = Open(fixture))
        {
            byte[] full = peer.Select()[4].Body;
            Entry(full, 5, 10014751, 2, true);
            for (int i = 0; i < committedEntries.Count; i++)
                That(EquipmentEntry(full, (short)(9 + i), true).SequenceEqual(committedEntries[i]),
                    "reconnect preserves the already committed reward rather than rerolling it");
            SendSelect(peer, 5, [16000], count: 2);
            ExpectAck(peer, 10014751, 5, [(16000, 2)]);
            byte[] delta = peer.Expect(0, 14).Body;
            Entry(delta, 5, -1, 0);
            CheckRandom(delta, 12);
            CheckRandom(delta, 13);
            string before = Snapshot();
            SendSelect(peer, 5, [16000], count: 2);
            peer.Expect(1, 160, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == before, "exhausted replay returns only failure and grants no extra equipment");
        }
        using (var peer = Open(fixture))
        {
            byte[] full = peer.Select()[4].Body;
            for (int i = 0; i < committedEntries.Count; i++)
                That(EquipmentEntry(full, (short)(9 + i), true).SequenceEqual(committedEntries[i]),
                    "reconnect restores every byte including fixed and random levels and original expiry");
            SetExpirationDebug(fixture, true);
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "cash-shop-usable-period on", out string relativeReply) &&
                relativeReply == "OK {\"ignoreUsablePeriod\":true}", "独立开启礼包相对期限忽略");
            SendSelect(peer, 6, [401000005]);
            ExpectAck(peer, 10005273, 6, [(401000005, 1)]);
            byte[] debugDelta = peer.Expect(0, 14).Body;
            That(U32(EquipmentEntry(debugDelta, 14), 56) == int.MaxValue,
                "相对忽略开启后，礼包内部60天装备的NOTI14下发协议上限");
            SetExpirationDebug(fixture, false);
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "cash-shop-usable-period off", out relativeReply) &&
                relativeReply == "OK {\"ignoreUsablePeriod\":false}", "独立关闭礼包相对期限忽略");
            long earliestExpiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60 * 86400;
            SendSelect(peer, 7, [401000005]);
            ExpectAck(peer, 10005273, 7, [(401000005, 1)]);
            byte[] normalDelta = peer.Expect(0, 14).Body;
            uint normalExpiry = U32(EquipmentEntry(normalDelta, 15), 56);
            That(normalExpiry >= earliestExpiry && normalExpiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60 * 86400,
                "GM关闭后，下一次开箱恢复原60天期限");
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            var full = peer.Select()[4].Body;
            That(U32(EquipmentEntry(full, 14, true), 56) == int.MaxValue && U32(EquipmentEntry(full, 15, true), 56) < int.MaxValue,
                "关闭与重连仍保留各次开箱实际提交的期限");
            peer.Barrier();
        }
        await fixture.StopAsync();
        That(fixture.Query("SELECT gold FROM characters") is [[12345L]] &&
            fixture.Query("SELECT COUNT(DISTINCT item_uid) FROM character_inventory_items") is [[7L]],
            "正常及调试的七个完整实例保留唯一UID，不追加费用");
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "equipment reward database integrity");

        void CheckRandom(byte[] body, short slot)
        {
            Entry(body, slot, 16000, InventoryRewardGrantService.InitialQuality);
            byte[] entry = EquipmentEntry(body, slot);
            That(entry[10] is >= 1 and <= 12 && entry[18] == 0 && entry[87] == 0 && U32(entry, 56) == 0 &&
                U16(entry, 11) == itemResources.Items.Get(16000).Durability, "random reward uses actual PVF levels in Attr only");
            committedEntries.Add(entry);
        }
        ItemCore Core(int id, int count = 1) => InventoryRewardGrantService.Create(itemResources.Items.Get(id), count);
        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT revision,gold FROM characters")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'"));
    }
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
}
