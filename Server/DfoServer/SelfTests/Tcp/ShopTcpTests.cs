using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class ShopTcpTests
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
        ushort specialRoleId;
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repo.Create(owner, new("ShopTcp", towns.Room, towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills,
                Inventory = starter with
                {
                    Gold = 1_000_000,
                    Items = [.. starter.Items,
                        new(0, new(InventoryListType.Main, 177), InventoryRewardGrantService.Create(itemResources.Items.Get(4237), 2)),
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
            specialRoleId = repo.Create(owner, new("ShopSpecial", towns.Room, towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills,
                Inventory = new([
                    SpecialItem(101550507, InventoryListType.Avatar, 0),
                    SpecialItem(101550507, InventoryListType.Equipment, 0),
                    SpecialItem(63022, InventoryListType.Pet, 0),
                    SpecialItem(63023, InventoryListType.Pet, 1)], Gold: 1000)
            }).Id;
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
            string beforeAuctionRejections = Snapshot();
            peer.Send(183, new byte[37]); peer.Expect(1, 183, [0, 210, 0]); peer.Barrier();
            peer.Send(184, new byte[9]); peer.Expect(1, 184, [0, 210, 0]); peer.Barrier();
            peer.Send(185, new byte[30]); peer.Expect(1, 185, [0, 210, 0]); peer.Barrier();
            That(Snapshot() == beforeAuctionRejections,
                "auction invalid requests keep the real u16 failure envelope and do not mutate inventory or wallet");
            string beforeForbiddenSale = Snapshot();
            Sell(peer, 0, 177, 1);
            peer.Expect(1, 22, [0, 1, 0]);
            peer.Barrier();
            That(Snapshot() == beforeForbiddenSale, "resource-forbidden sale preserves inventory, wallet and audit while the connection stays usable");
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
            peer.Barrier();
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
            Sell(peer, 0, 358, 1);
            gold += 20;
            peer.Expect(1, 22, Build(w => { w.Write((byte)1); w.Write(gold); w.Write((byte)0); w.Write((short)358); w.Write(1); }));
            delta = peer.Expect(0, 14).Body;
            Entry(delta, 0, 0, gold);
            Entry(delta, 358, 3037, 89);
            string rejectedCubeSale = Snapshot();
            foreach (int count in new[] { 0, 90 })
            {
                Sell(peer, 0, 358, count);
                peer.Expect(1, 22, [0, 1, 0]);
            }
            Sell(peer, 2, 358, 1);
            peer.Expect(1, 22, [0, 0xd6, 0]);
            peer.Barrier();
            That(Snapshot() == rejectedCubeSale, "rejected wallet sales preserve gold, cubes, revisions and audit");
            Sell(peer, 0, 358, 2);
            gold += 40;
            peer.Expect(1, 22, Build(w => { w.Write((byte)1); w.Write(gold); w.Write((byte)0); w.Write((short)358); w.Write(2); }));
            delta = peer.Expect(0, 14).Body;
            Entry(delta, 0, 0, gold);
            Entry(delta, 358, 3037, 87);
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
            foreach (var (slot, item, value) in new (short, int, int)[] { (5, 1106, 1000), (65, 1106, 997), (9, 27800, 999_999_998), (358, 3037, 87) })
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
            string beforeCubeFailure = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_tcp_cube_sale BEFORE INSERT ON currency_audit_log WHEN NEW.action_name='npc_shop_sell' AND NEW.currency_kind=7 BEGIN SELECT RAISE(ABORT,'cube sale failure'); END");
            Sell(peer, 0, 358, 1);
            peer.Closed();
            That(Snapshot() == beforeCubeFailure, "late cube sale failure closes without ACK and rolls back gold, wallet and both audits");
            fixture.Sql("DROP TRIGGER fail_tcp_cube_sale");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            Sell(peer, 0, 358, 1);
            gold += 20;
            peer.Expect(1, 22, Build(w => { w.Write((byte)1); w.Write(gold); w.Write((byte)0); w.Write((short)358); w.Write(1); }));
            var delta = peer.Expect(0, 14).Body;
            Entry(delta, 0, 0, gold);
            Entry(delta, 358, 3037, 86);
            Buy(peer, 3175, 1, 2, 1);
            That(U32(peer.Expect(1, 21).Body, 23) == 1, "reconnect can buy normally after rollback");
            Entry(peer.Expect(0, 14).Body, 3, 3175, 10);
            peer.Barrier();
            peer.Send(21, new byte[15]);
            peer.Closed();
        }
        string avatars = fixture.Snapshot("SELECT * FROM inventory_avatar_details ORDER BY item_uid");
        using (var peer = Open(fixture))
        {
            peer.Select(2);
            string before = Snapshot();
            foreach (byte list in new byte[] { 1, 3 })
            {
                Sell(peer, list, 0, 1);
                peer.Expect(1, 22, [0, 1, 0]);
                peer.Barrier();
                That(Snapshot() == before, "背包与穿戴时装均拒绝出售，不改变金币、实例、详情或审计");
            }
            fixture.Sql("CREATE TRIGGER fail_tcp_pet_sale BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'pet sale failure'); END");
            Sell(peer, 7, 0, 1);
            peer.Closed();
            That(Snapshot() == before, "宠物出售晚期提交失败不发送成功，金币、实例、详情与槽位全部回滚");
            fixture.Sql("DROP TRIGGER fail_tcp_pet_sale");
        }
        int specialGold = 1000;
        using (var peer = Open(fixture))
        {
            peer.Select(2);
            foreach (var (slot, itemId) in new (short, int)[] { (0, 63022), (1, 63023) })
            {
                long petUid = (long)fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=7 AND slot_index=$p1",
                    specialRoleId, slot)[0][0];
                var template = itemResources.Items.Get(itemId);
                int received = template.ShopPrice.GetSellGold(template.Durability, template.Durability);
                specialGold += received;
                Sell(peer, 7, slot, 1);
                peer.Expect(1, 22, Build(w => { w.Write((byte)1); w.Write(specialGold); w.Write((byte)7); w.Write(slot); w.Write(1); }));
                if (received != 0)
                    Entry(peer.Expect(0, 14).Body, 0, 0, specialGold);
                var delta = peer.Expect(0, 14).Body;
                That(delta[0] == 7, "宠物出售在成功 ACK 后清除 Pet 容器的实际源槽");
                Entry(delta, slot, -1, 0);
                peer.Barrier();
                That(fixture.Query("SELECT gold FROM characters WHERE id=$p0", specialRoleId) is [[long savedGold]] && savedGold == specialGold,
                    "宠物蛋和成宠按各自 PVF 价格出售，回执与余额一致");
                That(fixture.Query("SELECT item_uid FROM character_inventory_items WHERE item_uid=$p0", petUid).Length == 0 &&
                    fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE item_uid=$p0", petUid).Length == 0 &&
                    fixture.Query("SELECT item_uid FROM inventory_creature_details WHERE item_uid=$p0", petUid).Length == 0,
                    "宠物出售原子删除核心、槽位及完整详情，不遗留可复用的实例");
            }
        }
        using (var peer = Open(fixture))
        {
            peer.Select(2);
            peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_inventory_slots WHERE character_id=$p0 AND list_type=7", specialRoleId) is [[0L]] &&
                fixture.Query("SELECT gold FROM characters WHERE id=$p0", specialRoleId) is [[long savedGold]] && savedGold == specialGold &&
                fixture.Snapshot("SELECT * FROM inventory_avatar_details ORDER BY item_uid") == avatars,
                "重连后保留宠物出售结果与原时装详情，不恢复已售实例");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0, "shop TCP database integrity");
        await VerifyTargetBatchAndRoomAdmissionAsync(source, root, resources);
        await VerifyPeriodicLimitsAsync(source, root, resources);

        void Counts(TcpPeer peer)
        {
            peer.Send(715, Build(w => w.Write(limitedShop.Value.NpcId)));
            peer.Expect(1, 715, Build(w => { w.Write((byte)1); w.Write(1); w.Write(10099885); w.Write(3); w.Write(new byte[12]); }));
        }
        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT revision,gold FROM characters ORDER BY id")) +
            fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account") +
            fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY account,currency_kind") +
            fixture.Snapshot("SELECT * FROM currency_audit_log ORDER BY audit_id") +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_avatar_details ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_creature_details ORDER BY item_uid") +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM shop_purchase_counts ORDER BY item_id")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log"));

        InventoryItem SpecialItem(int itemId, InventoryListType list, short slot)
        {
            var template = itemResources.Items.Get(itemId);
            return InventoryRewardGrantService.CreateItem(template, new(list, slot),
                InventoryRewardGrantService.Create(template) with { ExpireTime = 0 });
        }
    }

    internal static async Task VerifyTargetBatchAndRoomAdmissionAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int beforeChecks = TestCheck.Count;
        const int item = 490705437, material = 490705400, shop = 400000097, npc = 400001107;
        const short firstMaterialSlot = 121, remainingMaterialSlot = 122;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "shop-target-batch"), resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        var starter = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0));
        var materialTemplate = resources.ItemResources.Items.Get(material);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repo.Create(owner, new("ShopBatch", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = starter with
                {
                    Gold = 1_000_000,
                    Items = [.. starter.Items,
                        InventoryRewardGrantService.CreateItem(materialTemplate, new(InventoryListType.Main, firstMaterialSlot),
                            InventoryRewardGrantService.Create(materialTemplate, 3)),
                        InventoryRewardGrantService.CreateItem(materialTemplate, new(InventoryListType.Main, remainingMaterialSlot),
                            InventoryRewardGrantService.Create(materialTemplate, 16))]
                }
            });
        long firstUid = (long)fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=1 AND list_type=0 AND slot_index=$p0", firstMaterialSlot)[0][0];
        long remainingUid = (long)fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=1 AND list_type=0 AND slot_index=$p0", remainingMaterialSlot)[0][0];
        long rewardUid = 0;
        short rewardSlot = -1;
        byte[] rewardCore = [], remainingCore = [];
        string committedAssets = "";
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            var initial = peer.Select();
            remainingCore = FullEntry(Main(initial), remainingMaterialSlot);
            BitConverter.GetBytes(11).CopyTo(remainingCore, 6);
            // 保留足够材料，成功后外城样本的材料与额度仍均合法，只改变房间准入条件。
            byte[] request = Convert.FromHexString("1D923F1D020000006184D7175388D717");
            That(request.Length == 16, "目标批量请求沿四个i32的真实16B线上合同");
            peer.Send(21, request);
            byte[] ack = peer.Expect(1, 21).Body;
            That(ack.Length == 149 && ack[0] == 1 && U32(ack, 1) == 1_000_000 && U32(ack, 5) == 0 &&
                U32(ack, 9) == 0 && U32(ack, 13) == 0 && U32(ack, 19) == item && U32(ack, 23) == 2 &&
                ack[140] == 1 && U32(ack, 141) == material && U32(ack, 145) == 0,
                "目标ACK21先返回四个钱包标量、完整123B核心、本次增量2及首个旧材料格余0");
            rewardSlot = checked((short)U16(ack, 17));
            rewardCore = ack.AsSpan(17, 123).ToArray();
            byte[] delta = peer.Expect(0, 14).Body;
            That(delta[0] == 0 && U16(delta, 1) == (rewardSlot == firstMaterialSlot ? 2 : 3),
                "成功ACK之后才发送全部实际扣料格与奖励的最终差量");
            if (rewardSlot != firstMaterialSlot) Entry(delta, firstMaterialSlot, -1, 0);
            Entry(delta, remainingMaterialSlot, material, 11);
            Entry(delta, rewardSlot, item, 2);
            Bytes(DeltaEntry(delta, rewardSlot), rewardCore, "目标NOTI14保留ACK的全部123B核心及最终奖励量2");
            Bytes(DeltaEntry(delta, remainingMaterialSlot), remainingCore, "第二材料格仅从16扣至11，保留其余完整核心字段");
            peer.Barrier();
            rewardUid = (long)fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=1 AND list_type=0 AND slot_index=$p0", rewardSlot)[0][0];
            CheckCommitted();
            Counts(peer);
            committedAssets = Assets();
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "target-tcp-batch", item, material, shop, npc, count = 2,
                requestLength = request.Length, ackLength = ack.Length, rewardSlot, rewardUid, remainingUid, materialRemaining = 11, permanentPurchased = 2 }));

            string beforeAlias = SqlState();
            Buy(peer, item, 2, shop, 400001112);
            peer.Expect(1, 21, [0, 17, 0]);
            peer.Barrier();
            That(SqlState() == beforeAlias, "房间内不把400001112当成目标NPC107别名，拒绝无任何SQL资产变化且连接可继续");

            peer.Send(1426);
            var location = peer.Expect(0, 23).Body;
            var returned = peer.Expect(0, 24).Body;
            var outer = resources.Towns.InitialReturn;
            That(U16(location) == peer.Id && location[2] == outer.Town && location[3] == outer.Area &&
                fixture.Query("SELECT town,area,x,y FROM characters WHERE id=1") is [[long savedTown, long savedArea, long savedX, long savedY]] &&
                savedTown == outer.Town && savedArea == outer.Area && savedX == U16(location, 4) && savedY == U16(location, 6),
                "真实CMD1426把目标角色送回保存的外城区，23与实际持久落点一致");
            Bytes(returned.AsSpan(6, 4), location.AsSpan(4, 4), "回城24与23的实际安全落点一致");
            peer.Barrier();
            That(Assets() == committedAssets, "离房只提交位置，不改变本次批量结果、UID或永久额度");
            string beforeOutside = SqlState();
            Buy(peer, item, 2, shop, npc);
            peer.Expect(1, 21, [0, 17, 0]);
            peer.Barrier();
            That(SqlState() == beforeOutside, "外城仍有11材料及6额度时同一目标批量请求拒绝17，无SQL增量且连接可继续");
            Counts(peer);
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "target-tcp-room-admission", aliasRejected = true, outsideRejected = true,
                materialStillSufficient = 11 >= 8, quotaStillSufficient = 8 - 2 >= 2, sqlUnchanged = SqlState() == beforeOutside }));
        }
        using (var peer = Open(fixture))
        {
            Restored(peer.Select(), "重连");
            Counts(peer);
            peer.Barrier();
        }
        await fixture.StopAsync();
        // 真正启动子进程；不以同进程重建ServerRuntime冒充持久恢复。
        await fixture.StartAsync("target-reopened", isolatedProcess: true);
        using (var peer = Open(fixture))
        {
            Restored(peer.Select(), "真实进程重启");
            Counts(peer);
            peer.Barrier();
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "target-tcp-process-restart", isolatedProcess = true, rewardUid,
                remainingUid, materialRemaining = 11, permanentPurchased = 2, fullCoreRestored = true, starterRegranted = false }));
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "目标批量TCP数据库完整性及外键检查");
        Console.WriteLine("T03_TARGET_SHOP_TCP_ASSERTIONS " + (TestCheck.Count - beforeChecks));

        void CheckCommitted()
        {
            That(rewardUid > 0 && rewardUid != firstUid && rewardUid != remainingUid &&
                fixture.Query("SELECT gold FROM characters WHERE id=1") is [[1_000_000L]] &&
                fixture.Query("SELECT count(*) FROM character_inventory_items WHERE item_uid=$p0", firstUid) is [[0L]] &&
                fixture.Query("SELECT s.item_uid,json_extract(i.item_core,'$.Value') FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid) WHERE s.character_id=1 AND s.list_type=0 AND s.slot_index=$p0 AND json_extract(i.item_core,'$.ItemId')=$p1", remainingMaterialSlot, material)
                    is [[long savedRemainingUid, 11L]] && savedRemainingUid == remainingUid &&
                fixture.Query("SELECT s.item_uid,json_extract(i.item_core,'$.Value') FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid) WHERE s.character_id=1 AND s.list_type=0 AND s.slot_index=$p0 AND json_extract(i.item_core,'$.ItemId')=$p1", rewardSlot, item)
                    is [[long savedRewardUid, 2L]] && savedRewardUid == rewardUid &&
                fixture.Query("SELECT count(*) FROM character_inventory_items WHERE character_id=1")[0][0] is long instances && instances == starter.Items.Length + 2 &&
                fixture.Query("SELECT character_id,npc_id,item_id,reset_daily,day,count FROM shop_purchase_counts WHERE account=$p0 ORDER BY item_id", fixture.Options.DevelopmentAccount)
                    is [[0L, 400001107L, 490705437L, 0L, 0L, 2L]],
                "目标持久提交只扣8材料奖2、保持第二格UID并创建真实奖励UID，限购仅账号永久day0/count2且不重发starter");
        }
        void Counts(TcpPeer peer)
        {
            peer.Send(715, Build(w => w.Write(npc)));
            peer.Expect(1, 715, Build(w => { w.Write((byte)1); w.Write(1); w.Write(item); w.Write(2); w.Write(new byte[12]); }));
        }
        void Restored(WirePacket[] packets, string phase)
        {
            byte[] full = Main(packets);
            Bytes(FullEntry(full, rewardSlot), rewardCore, phase + "完整NOTI13恢复奖励全部123B核心");
            Bytes(FullEntry(full, remainingMaterialSlot), remainingCore, phase + "完整NOTI13保留扣料后的核心与余11");
            That(!Enumerable.Range(0, U16(full, 3)).Any(index => U16(full, 5 + index * 123) == firstMaterialSlot && U32(full, 7 + index * 123) == material),
                phase + "不恢复已耗尽的首格材料");
            CheckCommitted();
            That(Assets() == committedAssets, phase + "保留全部库存核心、槽位、钱包、永久额度和审计，不重发starter或恢复材料");
        }
        static byte[] Main(WirePacket[] packets)
        {
            byte[] full = packets.Single(packet => packet.Kind == 0 && packet.Opcode == 13 && packet.Body[0] == 0).Body;
            That(full.Length == 5 + 123 * U16(full, 3), "主背包全量保留每条完整123B核心");
            return full;
        }
        static byte[] FullEntry(byte[] full, short slot)
        {
            int offset = Enumerable.Range(0, U16(full, 3)).Select(index => 5 + index * 123).Single(offset => U16(full, offset) == slot);
            return full.AsSpan(offset, 123).ToArray();
        }
        static byte[] DeltaEntry(byte[] delta, short slot)
        {
            int offset = Enumerable.Range(0, U16(delta, 1)).Select(index => 3 + index * 123).Single(offset => U16(delta, offset) == slot);
            return delta.AsSpan(offset, 123).ToArray();
        }
        string SqlState() => fixture.Snapshot() + Assets();
        string Assets() => fixture.Snapshot("SELECT gold FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account") +
            fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY account,currency_kind") +
            fixture.Snapshot("SELECT * FROM currency_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_avatar_details ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_creature_details ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM shop_purchase_counts ORDER BY item_id") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
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
