using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class ShopTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var itemResources = resources.ItemResources;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "shop"), resources: resources);
        var characters = resources.Characters;
        var towns = resources.Towns;
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        int weaponId = characters.Get(0).Equipment.Single(item => item.Slot == 12).Template.Id;
        int weaponSale = itemResources.Items.Get(weaponId).ShopPrice.SellGold;
        var cubeShop = itemResources.Shops.Shops.First(pair => pair.Value.Items.Contains(3033));
        var limitedShop = itemResources.Shops.Shops.First(pair => pair.Value.Items.Contains(10099885));
        var starter = InventoryRewardGrantService.CreateStarter(characters.Get(0));
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repo.Create(owner, new("ShopTcp", towns.Room, towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills,
                Inventory = starter with
                {
                    Gold = 1_000_000,
                    Items = [.. starter.Items,
                        new(0, new(InventoryListType.Main, 36), InventoryRewardGrantService.Create(itemResources.Items.Get(18400))),
                        new(0, new(InventoryListType.Main, 27), InventoryRewardGrantService.Create(itemResources.Items.Get(16400))),
                        new(0, new(InventoryListType.Main, 4), InventoryRewardGrantService.Create(itemResources.Items.Get(10400)) with { Durability = 16 }),
                        new(0, new(InventoryListType.PersonalCargo, 0), InventoryRewardGrantService.Create(itemResources.Items.Get(10400)) with { Durability = 0 }),
                        new(0, new(InventoryListType.Main, 5), InventoryRewardGrantService.Create(itemResources.Items.Get(1106), 998)),
                        new(0, new(InventoryListType.Main, 65), InventoryRewardGrantService.Create(itemResources.Items.Get(1106), 997))]
                }
            });
            InventoryService.Commit(repo, owner, current, new InventoryService(itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces).Grant(current, [new(3037, 100)]));
            repo.Create(owner, new("ShopSibling", towns.Room, towns.InitialReturn) { Level = 60, AcceptedStoryLevel = 1, Inventory = InventorySnapshot.Empty with { Gold = 1_000_000 } });
        }
        int gold = 1_000_000;
        long uid;
        long equipmentUid;
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            Buy(peer, 3175, 10, 2, 1);
            peer.Expect(1, 21, [0, 17, 0]);
            peer.Send(715, Build(w => w.Write(1)));
            peer.Expect(1, 715, [0, 17, 0]);
            peer.Select();
            // 前两笔对应现场满耐久拖鞋/腰带；期望值来自客户端原码，不从服务端计价函数反推。
            foreach (var (list, slot, received) in new (byte, short, int)[] { (0, 36, 25), (0, 27, 25), (0, 4, 30), (2, 0, 0) })
            {
                Sell(peer, list, slot, 1);
                gold += received;
                peer.Expect(1, 22, Build(w => { w.Write((byte)1); w.Write(gold); w.Write(list); w.Write(slot); w.Write(1); }));
                var saleDelta = peer.Expect(0, 14).Body;
                That(saleDelta[0] == list, "sale refreshes the actual source container");
                Entry(saleDelta, slot, -1, 0);
                if (received != 0)
                    Entry(saleDelta, 0, 0, gold);
                That(fixture.Query("SELECT gold FROM characters WHERE id=1") is [[long savedGold]] && savedGold == gold,
                    "sale ACK, delta and database use the native client price");
            }
            peer.Barrier();
            peer.Send(715, Build(w => w.Write(1)));
            peer.Expect(1, 715, [1, .. new byte[16]]);
            Buy(peer, 3175, 10, 2, 1);
            gold -= 100;
            var ack = peer.Expect(1, 21).Body;
            That(ack.Length == 141 && ack[0] == 1 && U32(ack, 1) == gold && U16(ack, 17) == 121 && U32(ack, 19) == 3175 && U32(ack, 23) == 10 && ack[140] == 0,
                "Client shop purchase has four scalars, 123B entry and material-count tail");
            var delta = peer.Expect(0, 14).Body;
            Entry(delta, 0, 0, gold);
            Entry(delta, 121, 3175, 10);
            uid = (long)fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=1 AND list_type=0 AND slot_index=121")[0][0];
            peer.Send(19, Build(w =>
            {
                w.Write((byte)0); w.Write((short)121); w.Write(3175); w.Write(0);
                w.Write((byte)0); w.Write((short)3); w.Write(0); w.Write(0); w.Write(-1); w.Write((ushort)0);
            }));
            peer.Expect(1, 19, [1, 0, 121, 0, 10, 0, 0, 0, 0, 3, 0]);
            delta = peer.Expect(0, 14).Body;
            Entry(delta, 3, 3175, 10);
            Entry(delta, 121, -1, 0);
            Sell(peer, 0, 3, 3);
            gold += 3;
            peer.Expect(1, 22, Build(w => { w.Write((byte)1); w.Write(gold); w.Write((byte)0); w.Write((short)3); w.Write(3); }));
            delta = peer.Expect(0, 14).Body;
            Entry(delta, 3, 3175, 7);
            Entry(delta, 0, 0, gold);
            Buy(peer, 3175, 2, 2, 1);
            gold -= 20;
            ack = peer.Expect(1, 21).Body;
            That(U16(ack, 17) == 3 && U32(ack, 23) == 2, "purchase stacks into quickbar and ACK carries only this increment");
            delta = peer.Expect(0, 14).Body;
            Entry(delta, 3, 3175, 9);
            Entry(delta, 0, 0, gold);
            That(Equals(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=1 AND list_type=0 AND slot_index=3")[0][0], uid),
                "quickbar buy and sell preserve original UID");
            Buy(peer, 3175, 1, 2, 3);
            peer.Expect(1, 21, [0, 17, 0]);
            string invalidCountBefore = Snapshot();
            foreach (int count in new[] { int.MinValue, -1, 0, 1_000_001, int.MaxValue })
            {
                Buy(peer, 3175, count, 2, 1);
                peer.Expect(1, 21, [0, 0x85, 0]);
            }
            peer.Barrier();
            That(Snapshot() == invalidCountBefore, "invalid purchase quantities leave gold, items, counters and audit unchanged");
            Buy(peer, 1106, 10, 15, 2);
            peer.Expect(1, 21, [0, 4, 0]);
            peer.Barrier();
            That(Snapshot() == invalidCountBefore, "stack capacity rejection is visible and leaves all assets and purchase counters unchanged");
            Buy(peer, 1106, 2, 15, 2);
            gold -= 200;
            ack = peer.Expect(1, 21).Body;
            That(U32(ack, 1) == gold && U16(ack, 17) == 5 && U32(ack, 19) == 1106 && U32(ack, 23) == 2,
                "purchase succeeds after rejection and ACK carries the actual granted count");
            delta = peer.Expect(0, 14).Body;
            Entry(delta, 0, 0, gold);
            Entry(delta, 5, 1106, 1000);
            That(U16(delta, 1) == 2 && fixture.Query("SELECT SUM(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE character_id=1 AND json_extract(item_core,'$.ItemId')=1106") is [[1997L]],
                "purchase updates only wallet and first compatible stack without creating a new slot");
            // test 在普通 NPC 商店购买破旧的佩刀的实际16B请求，数量字段为0。
            peer.Send(21, Convert.FromHexString("986C0000000000000200000001000000"));
            gold -= 3500;
            ack = peer.Expect(1, 21).Body;
            That(ack.Length == 141 && ack[0] == 1 && U32(ack, 1) == gold && U16(ack, 17) == 9 &&
                U32(ack, 19) == 27800 && U32(ack, 23) == 999_999_998 && ack[140] == 0,
                "captured zero-count equipment request returns the charged balance and complete equipment core");
            delta = peer.Expect(0, 14).Body;
            Entry(delta, 0, 0, gold);
            Entry(delta, 9, 27800, 999_999_998);
            That(fixture.Query("SELECT item_uid FROM character_inventory_items WHERE character_id=1 AND json_extract(item_core,'$.ItemId')=27800") is [[long savedEquipmentUid]] && savedEquipmentUid > 0,
                "zero-count equipment purchase commits exactly one persistent instance");
            equipmentUid = (long)fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=1 AND list_type=0 AND slot_index=9")[0][0];
            peer.Send(267, [3, 12, 0]);
            peer.Expect(1, 267, [1, 3, 12, 0]);
            peer.Expect(0, 251, [1, 0, 3, 12, 0, 1]);
            Sell(peer, 3, 12, 1);
            peer.Expect(1, 22, [0, 0xd6, 0]);
            peer.Send(268, [3, 12, 0]);
            peer.Expect(1, 268, [1, 3, 12, 0, 0, 0, 0, 0]);
            peer.Expect(0, 252, [3, 12, 0]);
            Sell(peer, 3, 12, 100);
            gold += weaponSale;
            peer.Expect(1, 22, Build(w => { w.Write((byte)1); w.Write(gold); w.Write((byte)3); w.Write((short)12); w.Write(1); }));
            Entry(peer.Expect(0, 14).Body, 0, 0, gold);
            Entry(peer.Expect(0, 14).Body, 12, -1, 0);
            peer.Expect(0, 2);
            peer.Expect(0, 2);
            Buy(peer, 3033, 2, cubeShop.Key, cubeShop.Value.NpcId);
            ack = peer.Expect(1, 21).Body;
            That(ack.Length == 149 && U16(ack, 17) == 354 && U32(ack, 23) == 2 && ack[140] == 1 && U32(ack, 141) == 3037 && U32(ack, 145) == 90,
                "wallet exchange reply preserves reward increment and absolute material remainder");
            delta = peer.Expect(0, 14).Body;
            Entry(delta, 354, 3033, 2);
            Entry(delta, 358, 3037, 90);
            Buy(peer, 10099885, 3, limitedShop.Key, limitedShop.Value.NpcId);
            gold -= 90_000;
            ack = peer.Expect(1, 21).Body;
            That(U32(ack, 1) == gold && U32(ack, 19) == 10099885 && U32(ack, 23) == 3, "limited purchase commits whole price and quantity");
            peer.Expect(0, 14);
            Buy(peer, 10099885, 1, limitedShop.Key, limitedShop.Value.NpcId);
            peer.Expect(1, 21, [0, 0x85, 0]);
            Counts(peer);
            peer.Barrier();
            peer.Send(7);
            peer.Expect(1, 7, [1]);
            peer.Expect(0, 2);
            peer.Select(1);
            Counts(peer);
            Buy(peer, 10099885, 1, limitedShop.Key, limitedShop.Value.NpcId);
            peer.Expect(1, 21, [0, 0x85, 0]);
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            var entry = peer.Select();
            byte[] full = entry.Single(packet => packet.Kind == 0 && packet.Opcode == 13 && packet.Body[0] == 0).Body;
            foreach (var (slot, item, value) in new (short, int, int)[] { (5, 1106, 1000), (65, 1106, 997), (9, 27800, 999_999_998) })
            {
                int offset = Enumerable.Range(0, U16(full, 3)).Select(index => 5 + index * 123).Single(offset => U16(full, offset) == slot);
                That(U32(full, offset + 2) == item && U32(full, offset + 6) == value, "complete purchased item survives reconnect at slot " + slot);
            }
            That(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=1 AND list_type=0 AND slot_index=9") is [[long restoredEquipmentUid]] && restoredEquipmentUid == equipmentUid,
                "zero-count equipment purchase keeps its UID after reconnect");
            That(fixture.Query("SELECT gold FROM characters WHERE id=1") is [[long savedGold]] && savedGold == gold,
                "native-priced sales and purchases retain the exact balance after reconnect");
            Counts(peer);
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_tcp_shop BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'shop failure'); END");
            Buy(peer, 3175, 1, 2, 1);
            peer.Closed();
            That(Snapshot() == before, "failed buy closes without ACK or any persisted gold, item or counter change");
            fixture.Sql("DROP TRIGGER fail_tcp_shop");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            Buy(peer, 3175, 1, 2, 1);
            That(U32(peer.Expect(1, 21).Body, 23) == 1, "reconnect can buy normally after rollback");
            Entry(peer.Expect(0, 14).Body, 3, 3175, 10);
            peer.Barrier();
            peer.Send(21, new byte[15]);
            peer.Closed();
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0, "shop TCP database integrity");

        void Counts(TcpPeer peer)
        {
            peer.Send(715, Build(w => w.Write(limitedShop.Value.NpcId)));
            peer.Expect(1, 715, Build(w => { w.Write((byte)1); w.Write(1); w.Write(10099885); w.Write(3); w.Write(new byte[12]); }));
        }
        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT revision,gold FROM characters ORDER BY id")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM shop_purchase_counts ORDER BY item_id")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log"));
    }

    static void Buy(TcpPeer peer, int item, int count, int shop, int npc) => peer.Send(21, Build(w => { w.Write(item); w.Write(count); w.Write(shop); w.Write(npc); }));
    static void Sell(TcpPeer peer, byte list, short slot, int count) => peer.Send(22, Build(w =>
    {
        w.Write(list); w.Write(slot); w.Write(count); w.Write(1); w.Write(2); w.Write(unchecked(2 * (slot + count + list) + 1));
    }));
    static void Entry(byte[] delta, short slot, int item, int value)
    {
        That(delta.Length == 3 + 123 * U16(delta, 1), "delta preserves every complete entry");
        int offset = Enumerable.Range(0, U16(delta, 1)).Select(index => 3 + index * 123).Single(offset => U16(delta, offset) == slot);
        That(U32(delta, offset + 2) == unchecked((uint)item) && U32(delta, offset + 6) == value, "delta slot " + slot + " has committed item and amount");
    }
}
