using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class InventoryTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var itemResources = resources.ItemResources;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "inventory"));
        var characters = resources.Characters;
        var towns = resources.Towns;
        var owners = new AccountOwners();
        var repository = new CharacterRepository(fixture.Database);
        ItemTemplate stack;
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            stack = PvfScript.List(resources.Archive.ReadText("stackable/stackable.lst")).Keys.Where(id => id > 2)
                .Select(id => TryGet(id)).First(item => item is { Kind: ItemKind.Consumable, StackLimit: >= 30, Expiration: { AbsoluteTime: 0, Days: 0 } })!;
            var character = new GameSession(repository, towns, owner).CreateCharacter("BagStackTest", characters.Get(0));
            var inventory = new InventoryService(itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
            InventoryService.Commit(repository, owner, character, inventory.Grant(character, [new(stack.Id, 20)]));
        }
        ItemTemplate? TryGet(int id)
        {
            try
            { return itemResources.Items.Get(id); }
            catch (InvalidDataException) { return null; }
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            var entry = peer.Select();
            That(entry[4].Body.Length == 1973 && U16(entry[4].Body, 3) == 16 && U16(entry[4].Body, 374) == 65 && U32(entry[4].Body, 380) == 20, "full inventory contains virtual balances and preseeded stack");
            var split = MoveItem(0, 65, stack.Id, 7, 0, 66, 0);
            peer.Send(19, split);
            peer.Expect(1, 19, MoveAck(0, 65, 7, 0, 66));
            var delta = peer.Expect(0, 14).Body;
            That(delta.Length == 249 && U32(delta, 9) == 13 && U32(delta, 132) == 7, "split delta reports exact two counts");
            string before = Snapshot(fixture);
            peer.Send(19, split);
            peer.Expect(1, 19, [0, 23, 0, 0, 0]);
            peer.Barrier();
            That(Snapshot(fixture) == before, "replayed TCP split causes no duplicate core, audit or revision");
            peer.Send(19, MoveItem(0, 66, stack.Id, 0, 0, 65, stack.Id));
            peer.Expect(1, 19, MoveAck(0, 66, 7, 0, 65));
            delta = peer.Expect(0, 14).Body;
            That(U32(delta, 9) == 20 && U32(delta, 128) == uint.MaxValue, "merge clears consumed slot and retains sum");
            peer.Send(19, MoveItem(0, 65, stack.Id, 0, 0, 3, 0));
            peer.Expect(1, 19, MoveAck(0, 65, 20, 0, 3));
            peer.Expect(0, 14);
            peer.Barrier();
            That(fixture.Query("SELECT slot_index FROM character_inventory_slots WHERE list_type=0") is [[3L]], "consumable moves into quick slot");
            peer.Send(7);
            peer.Expect(1, 7, [1]);
            peer.Expect(0, 2);
            peer.Create("BagGearTest");
            var selected = peer.Select(1);
            That(selected[2].Body[133 + NameWidth("BagGearTest")] == 5 && selected[3].Body[126] == 5, "created character actor and player include five PVF equipment items");
            int quality = 999999998;
            ushort characterId = peer.Id;
            int weaponId = characters.Get(0).Equipment.Single(item => item.Slot == 12).Template.Id;
            int pantsId = characters.Get(0).Equipment.Single(item => item.Slot == 17).Template.Id;
            var quickUid = fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=3 AND slot_index=17", characterId)[0][0];
            peer.Send(19, MoveItem(0, 21, 0, 1, 3, 17, pantsId));
            peer.Expect(1, 19, MoveAck(0, 21, 1, 3, 17));
            DeltaAndAppearance(peer, 4);
            var quickRequest = Convert.FromHexString("0003000000000000000000001500E047000000000000FFFFFFFF0000");
            peer.Send(19, quickRequest);
            peer.Expect(1, 19, Convert.FromHexString("0100030001000000001500"));
            var quickDelta = peer.Expect(0, 14).Body;
            That(quickDelta.Length == 249 && U16(quickDelta, 3) == 3 && U32(quickDelta, 5) == 18400 &&
                U16(quickDelta, 126) == 21 && U32(quickDelta, 128) == uint.MaxValue, "captured empty-first request places equipment into quick slot and clears page slot");
            That(Equals(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=3", characterId)[0][0], quickUid), "equipment quick slot retains persisted UID");
            string quickSnapshot = Snapshot(fixture);
            peer.Send(19, quickRequest);
            peer.Expect(1, 19, [0, 23, 0, 0, 0]);
            peer.Barrier();
            That(Snapshot(fixture) == quickSnapshot, "quick slot replay leaves stored equipment unchanged");
            peer.Send(20, Convert.FromHexString("080000000814100018012000"));
            peer.Expect(1, 20, Convert.FromHexString("010400000008001000"));
            peer.Barrier();
            That(Snapshot(fixture) == quickSnapshot, "equipment page sort keeps quick slot equipment unchanged");
            peer.Send(7);
            peer.Expect(1, 7, [1]);
            peer.Expect(0, 2);
            var reselected = peer.Select(1);
            That(reselected[3].Body[126] == 4 && reselected[4].Body.Length == 1973 && U16(reselected[4].Body, 374) == 3 &&
                U32(reselected[4].Body, 376) == 18400, "reselect restores equipment in quick slot through full inventory");
            peer.Send(19, MoveItem(0, 3, pantsId, 1, 3, 17, 0));
            peer.Expect(1, 19, MoveAck(0, 3, 1, 3, 17));
            DeltaAndAppearance(peer, 5);
            peer.Send(19, MoveItem(0, 8, 0, 1, 3, 17, pantsId));
            peer.Expect(1, 19, MoveAck(0, 8, 1, 3, 17));
            DeltaAndAppearance(peer, 4);
            peer.Send(19, MoveItem(0, 8, pantsId, 0, 0, 3, 0));
            peer.Expect(1, 19, MoveAck(0, 8, 1, 0, 3));
            quickDelta = peer.Expect(0, 14).Body;
            That(U16(quickDelta, 3) == 3 && U32(quickDelta, 5) == 18400 && U16(quickDelta, 126) == 8 &&
                U32(quickDelta, 128) == uint.MaxValue, "equipment moves between first and last quick slot");
            peer.Send(19, MoveItem(0, 3, pantsId, 1, 3, 17, 0));
            peer.Expect(1, 19, MoveAck(0, 3, 1, 3, 17));
            DeltaAndAppearance(peer, 5);
            string beforeCaptured = Snapshot(fixture);
            peer.Send(19, MoveItem(0, 9, 0, 1, 3, 16, quality));
            peer.Expect(1, 19, [0, 23, 0, 0, 3]);
            peer.Barrier();
            That(Snapshot(fixture) == beforeCaptured, "quality echo rejects without mutation or area restriction error");
            var jacketUid = fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=3 AND slot_index=16", characterId)[0][0];
            var captured = Convert.FromHexString("00090000000000010000000310007030000000000000FFFFFFFF0000");
            peer.Send(19, captured);
            peer.Expect(1, 19, Convert.FromHexString("0100090001000000031000"));
            DeltaAndAppearance(peer, 4);
            That(Equals(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=9", characterId)[0][0], jacketUid), "captured empty-first unequip commits original jacket UID");
            string afterCaptured = Snapshot(fixture);
            peer.Send(19, captured);
            peer.Expect(1, 19, [0, 23, 0, 0, 3]);
            peer.Barrier();
            That(Snapshot(fixture) == afterCaptured, "replayed empty-first unequip cannot re-equip the jacket");
            peer.Send(19, MoveItem(0, 9, 12400, 1, 3, 16, 0));
            peer.Expect(1, 19, MoveAck(0, 9, 1, 3, 16));
            DeltaAndAppearance(peer, 5);
            var originalUid = fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=3 AND slot_index=12", characterId)[0][0];
            peer.Send(19, MoveItem(0, 9, 0, 1, 3, 12, weaponId));
            peer.Expect(1, 19, MoveAck(0, 9, 1, 3, 12));
            DeltaAndAppearance(peer, 4);
            That(Equals(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=9", characterId)[0][0], originalUid), "unequip persists original UID before ACK");
            peer.Send(20, Convert.FromHexString("080000000814100018012000"));
            peer.Expect(1, 20, Convert.FromHexString("010400000008001000"));
            peer.Barrier();
            peer.Send(19, MoveItem(0, 9, weaponId, 1, 3, 12, 0));
            peer.Expect(1, 19, MoveAck(0, 9, 1, 3, 12));
            DeltaAndAppearance(peer, 5);
            string beforeReplay = Snapshot(fixture);
            peer.Send(19, MoveItem(0, 9, weaponId, 1, 3, 12, 0));
            peer.Expect(1, 19, [0, 23, 0, 0, 3]);
            peer.Barrier();
            That(Snapshot(fixture) == beforeReplay, "replayed equip is read-only and keeps connection");
            peer.Send(19, MoveItem(0, 9, 0, 1, 3, 12, weaponId));
            peer.Expect(1, 19, MoveAck(0, 9, 1, 3, 12));
            DeltaAndAppearance(peer, 4);
            peer.Send(19, MoveItem(0, 10, 0, 1, 3, 17, pantsId));
            peer.Expect(1, 19, MoveAck(0, 10, 1, 3, 17));
            DeltaAndAppearance(peer, 3);
            peer.Send(20, Convert.FromHexString("080000000814100018012000"));
            peer.Expect(1, 20, Convert.FromHexString("010400000008001000"));
            var cleared = peer.Expect(0, 14).Body;
            var sorted = peer.Expect(0, 14).Body;
            That(cleared.Length == 249 && U32(cleared, 5) == uint.MaxValue && U32(cleared, 128) == uint.MaxValue, "sorting clears replaced resource objects first");
            That(U32(sorted, 5) == 18400 && U32(sorted, 128) == 101010912, "sorting follows item IDs and retains complete objects");
            byte[] discard = DeleteItem(9, quality);
            peer.Send(18, discard);
            peer.Expect(1, 18, Convert.FromHexString("010C000000080012060809100018001800"));
            var removed = peer.Expect(0, 14).Body;
            That(U16(removed, 3) == 9 && U32(removed, 5) == uint.MaxValue, "protobuf discard clears exact main slot");
            beforeReplay = Snapshot(fixture);
            peer.Send(18, discard);
            peer.Expect(1, 18, Convert.FromHexString("000400000008001801"));
            peer.Barrier();
            That(Snapshot(fixture) == beforeReplay, "replayed discard does not mutate storage");
            peer.Send(19, MoveItem(0, 8, 0, 0, 0, 10, weaponId));
            peer.Expect(1, 19, MoveAck(0, 8, 1, 0, 10));
            peer.Expect(0, 14);
        }
        await fixture.StopAsync();
        await fixture.StartAsync("reopened");
        using (var peer = Open(fixture))
        {
            var packets = peer.Select(1);
            That(packets[3].Body[126] == 3 && packets[4].Body.Length == 1973 && U16(packets[4].Body, 374) == 8 && U32(packets[4].Body, 376) == 101010912, "restart restores quick slot equipment without starter regrant");
        }
        That(fixture.Query("SELECT count(*) FROM character_inventory_items WHERE character_id=2") is [[4L]], "deleted equipment never returns on restart");
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]], "TCP inventory database integrity");
        await fixture.StopAsync();
        await VerifyRulesAsync(source, Path.Combine(root, "inventory-rules"), characters, towns, stack);
        await VerifyLocksAsync(source, Path.Combine(root, "inventory-locks"), characters, towns);
        Console.WriteLine("PASS C# TCP inventory: full list, split, merge, quick slot, equip, sort, discard, replay and restart");
    }

    static async Task VerifyRulesAsync(ServerOptions source, string root, CharacterCatalog characters, TownCatalog towns, ItemTemplate stack)
    {
        await using var fixture = new TcpTestFixture(source, root);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var character = new GameSession(repository, towns, owner).CreateCharacter("BagRuleTest", characters.Get(0));
            var items = character.Inventory.Items.Select(item => item.Position.Index switch
            {
                12 => item with { Position = new(InventoryListType.Main, 20), Core = item.Core with { EquipmentLockId = 7 } },
                14 => item with { Position = new(InventoryListType.Main, 21), Core = item.Core with { EquipmentLockId = 8, SortLockFlag = 1, ExpireTime = 1 } },
                _ => item
            });
            InventoryItem[] stacks = [
                new(0, new(InventoryListType.Main, 65), InventoryRewardGrantService.Create(stack, 20) with { SortLockFlag = 1 }),
                new(0, new(InventoryListType.Main, 66), InventoryRewardGrantService.Create(stack, stack.StackLimit - 2))];
            repository.CommitInventory(owner, character, new(character.Inventory, new([.. items, .. stacks]), "fixture_inventory_rules"));
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            var entry = peer.Select(inventoryNotices: [(1, 713), (0, 251)]);
            Bytes(entry[7].Body, [1, 0, 65, 0], "sorting lock restored after item lists");
            Bytes(entry[8].Body, [1, 0, 0, 20, 0, 1], "only unexpired equipment lock restored");
            That(U16(entry[4].Body, 3) == 18 && fixture.Query("SELECT count(*) FROM inventory_audit_log WHERE action_name='item_expired'") is [[1L]],
                "selection expires locked item before inventory projection");
            string before = Snapshot(fixture);
            peer.Send(19, MoveItem(0, 65, stack.Id, 5, 0, 66, stack.Id));
            peer.Expect(1, 19, [0, 23, 0, 0, 0]);
            peer.Barrier();
            That(Snapshot(fixture) == before, "insufficient merge space preserves all database rows and revision");
            peer.Send(19, MoveItem(0, 65, stack.Id, 2, 0, 66, stack.Id));
            peer.Expect(1, 19, MoveAck(0, 65, 2, 0, 66));
            var merged = peer.Expect(0, 14).Body;
            That(U32(merged, 9) == 18 && U32(merged, 132) == stack.StackLimit && merged[98] == 1,
                "merge uses exact count despite differing sort locks");
            peer.Expect(1, 713, [1, 0, 65, 0]);
            peer.Send(19, MoveItem(0, 65, stack.Id, 0, 0, 4, 0));
            peer.Expect(1, 19, MoveAck(0, 65, 18, 0, 4));
            var moved = peer.Expect(0, 14).Body;
            That(U16(moved, 3) == 4 && moved[98] == 0, "quick slot delta clears only sorting lock");
            peer.Expect(1, 714, [1, 0, 4, 0]);
            peer.Send(20, Convert.FromHexString("0900000008141000180120FF01"));
            peer.Expect(1, 20, Convert.FromHexString("010400000008001000"));
            var sorted = peer.Expect(0, 14).Body;
            That(U16(sorted, 1) == 2 && U16(sorted, 3) == 9 && sorted[99] == 7 && U32(sorted, 128) == uint.MaxValue,
                "fallback sort moves equipment safety lock without reviving expired item");
            peer.Expect(0, 251, [1, 0, 0, 9, 0, 1]);
            before = Snapshot(fixture);
            peer.Send(18, DeleteItem(9, InventoryRewardGrantService.InitialQuality));
            peer.Expect(1, 18, Convert.FromHexString("000400000008001801"));
            peer.Barrier();
            That(Snapshot(fixture) == before, "sorting does not release equipment deletion protection");
            peer.Send(18, DeleteItem(4, 18, 0));
            peer.Expect(1, 18, Convert.FromHexString("010C000000080012060804100018001800"));
            peer.Expect(0, 14);
            peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_inventory_items") is [[5L]], "discard zero removes entire stack and expired item stays removed");
            peer.Send(18, DeleteItem(16, InventoryRewardGrantService.InitialQuality, 0, 3));
            peer.Expect(1, 18, Convert.FromHexString("010C000000080312060810100018001800"));
            var wearClear = peer.Expect(0, 14).Body;
            That(wearClear[0] == 3 && U16(wearClear, 3) == 16 && U32(wearClear, 5) == uint.MaxValue, "discard worn item clears equipment container");
            var actor = peer.Expect(0, 2).Body;
            var player = peer.Expect(0, 2).Body;
            That(actor[133 + NameWidth("BagRuleTest")] == 2 && player[126] == 2, "worn discard refreshes committed appearance");
            before = Snapshot(fixture);
            peer.Send(18, DeleteItem(16, InventoryRewardGrantService.InitialQuality, 0, 3));
            peer.Expect(1, 18, Convert.FromHexString("000400000008031801"));
            peer.Barrier();
            That(Snapshot(fixture) == before, "replayed worn discard does not write or refresh appearance");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("reopened");
        using (var peer = Open(fixture))
        {
            var entry = peer.Select(inventoryNotices: [(0, 251)]);
            That(entry[3].Body[126] == 2 && U16(entry[4].Body, 3) == 17 && U16(entry[4].Body, 374) == 9 && entry[4].Body[470] == 7,
                "restart preserves sorted equipment safety lock and remaining stack");
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "inventory rule changes retain SQLite integrity and instance ownership");
    }

    static string Snapshot(TcpTestFixture fixture) => JsonSerializer.Serialize(new
    {
        Characters = fixture.Query("SELECT * FROM characters ORDER BY id"),
        Items = fixture.Query("SELECT * FROM character_inventory_items ORDER BY item_uid"),
        Slots = fixture.Query("SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index"),
        Audit = fixture.Query("SELECT * FROM inventory_audit_log ORDER BY audit_id")
    });
    static void DeltaAndAppearance(TcpPeer peer, byte count)
    {
        var main = peer.Expect(0, 14).Body;
        var equipment = peer.Expect(0, 14).Body;
        That(main[0] == 0 && equipment[0] == 3 && main.Length == 126 && equipment.Length == 126, "main and worn slot deltas follow ACK");
        var actor = peer.Expect(0, 2).Body;
        var player = peer.Expect(0, 2).Body;
        That(actor[133 + NameWidth("BagGearTest")] == count && player[126] == count && player.Length == 203 + count * 91, "appearance and player agree after wear change");
    }
    static byte[] MoveItem(byte sourceList, short sourceSlot, int sourceItemId, int count, byte targetList, short targetSlot, int targetItemId) => Build(writer =>
    {
        writer.Write(sourceList);
        writer.Write(sourceSlot);
        writer.Write(sourceItemId);
        writer.Write(count);
        writer.Write(targetList);
        writer.Write(targetSlot);
        writer.Write(targetItemId);
        writer.Write(0);
        writer.Write(-1);
        writer.Write((ushort)0);
    });
    static byte[] MoveAck(byte sourceList, short sourceSlot, int count, byte targetList, short targetSlot) => Build(writer =>
    {
        writer.Write((byte)1);
        writer.Write(sourceList);
        writer.Write(sourceSlot);
        writer.Write(count);
        writer.Write(targetList);
        writer.Write(targetSlot);
    });
    static byte[] DeleteItem(short slot, int value, byte count = 1, byte list = 0)
    {
        var child = Build(writer => { writer.Write(new byte[] { 8, 0, 16, (byte)slot, 24 }); Varint(writer, (uint)value); writer.Write(new byte[] { 32, count }); });
        var payload = Build(writer => { writer.Write(new byte[] { 8, 18, 16, list, 26, (byte)child.Length }); writer.Write(child); writer.Write(new byte[] { 32, 0 }); });
        return Build(writer => { writer.Write(payload.Length); writer.Write(payload); });
    }
    static void Varint(BinaryWriter writer, uint value)
    {
        while (value >= 128)
        {
            writer.Write((byte)((value & 127) | 128));
            value >>= 7;
        }
        writer.Write((byte)value);
    }
}
