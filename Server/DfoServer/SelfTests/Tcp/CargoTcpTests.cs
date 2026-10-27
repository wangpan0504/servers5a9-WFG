using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class CargoTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var itemResources = resources.ItemResources;
        Inventory.CargoTests.RunCore();
        Inventory.CargoTests.Run(resources.Towns, resources.Characters, itemResources);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "cargo"), resources: resources);
        var characters = resources.Characters;
        var towns = resources.Towns;
        var owners = new AccountOwners();
        var repo = new CharacterRepository(fixture.Database);
        int weaponId = characters.Get(0).Equipment.Single(item => item.Slot == 12).Template.Id;
        long weaponUid;
        string weaponCore;
        long mirrorUid;
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repo.Create(owner, new("CargoTcp", towns.Room, towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills,
                Inventory = InventoryRewardGrantService.CreateStarter(characters.Get(0))
            });
            var items = new InventoryService(itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
            current = InventoryService.Commit(repo, owner, current, items.Grant(current, [new(0, 20_000_000), new(3299, 600), new(6, 1)]));
            current = InventoryService.Commit(repo, owner, current, new CurrencyService(itemResources.GoldLimits).Grant(current, CurrencyKind.Cera, 5000));
            var weapon = current.Inventory.Find(new(InventoryListType.Equipment, 12))!;
            var core = weapon.Core with { Attr = 11, Rune = 3, AmplifyValue = 25, Random0 = new(1, 2, 3), Extension114 = new(1, 222) };
            current = repo.CommitInventory(owner, current, new(current.Inventory,
                current.Inventory with { Items = current.Inventory.Items.Replace(weapon, weapon with { Core = core }) }, "cargo_fixture"));
            current = InventoryService.Commit(repo, owner, current, items.Move(current,
                new(new(InventoryListType.Equipment, 12), weaponId, 1, new(InventoryListType.Main, 9), 0, 0, -1, 0, 0)));
            weaponUid = weapon.Uid;
            weaponCore = JsonSerializer.Serialize(core);
            current = repo.CommitInventory(owner, current, new(current.Inventory,
                current.Inventory with { Items = current.Inventory.Items.Add(new(0, new(InventoryListType.Main, 13), core)) }, "cargo_echo_fixture"));
            mirrorUid = current.Inventory.Find(new(InventoryListType.Main, 13))!.Uid;
            repo.Create(owner, new("CargoSibling", towns.Room, towns.InitialReturn) { AcceptedStoryLevel = 1 });
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            var entered = peer.Select();
            Bytes(entered[6].Body, [2, 8, 0, 0, 0, 0], "Client personal cargo includes its auxiliary count after the empty list");
            Bytes(entered[7].Body, [12, 0, 0, 0, 0, 0, 0, 0, 0], "closed account cargo initializes zero money and no items");
            peer.Send(305);
            peer.Expect(1, 305, [1]);
            ExpectGold(peer, 19_900_000);
            Cargo(peer.Expect(0, 13).Body, 12, 1, 0, 0);
            peer.Barrier();
            That(fixture.Query("SELECT capacity FROM account_cargo") is [[1L]],
                "305开通一格并同步实际费用与权威容量");
            peer.Send(305);
            peer.Expect(1, 305, [0, 20, 0]);
            peer.Barrier();
            peer.Send(306);
            peer.Expect(1, 306, [1]);
            ExpectGold(peer, 17_900_000);
            Cargo(peer.Expect(0, 13).Body, 12, 8, 0, 0);
            peer.Barrier();
            That(fixture.Query("SELECT capacity FROM account_cargo") is [[8L]],
                "原生306由客户端推进八格，随后的第七格存取无需重选角色");
            peer.Send(307, Build(w => w.Write(1000)));
            peer.Expect(1, 307, [1, 232, 3, 0, 0]);
            ExpectGold(peer, 17_899_000);
            peer.Send(308, Build(w => w.Write(250)));
            peer.Expect(1, 308, [1, 238, 2, 0, 0]);
            ExpectGold(peer, 17_899_250);
            peer.Send(308, Build(w => w.Write(751)));
            peer.Expect(1, 308, [0, 10, 0]);
            peer.Barrier();
            Transfer(peer, 0, 9, weaponId, 1, 2, 7, 7, 1);
            var main = peer.Expect(0, 14).Body;
            That(main[0] == 0 && U32(main, 5) == uint.MaxValue, "personal deposit clears original physical slot");
            var personal = peer.Expect(0, 14).Body;
            That(personal[0] == 2 && U16(personal, 3) == 7 && U32(personal, 5) == weaponId && personal.Length == 126,
                "personal deposit uses complete 123-byte entry");
            SortPersonal(peer, weaponId);
            peer.Send(20, Convert.FromHexString("06000000100218142000"));
            peer.Expect(1, 20, [1, 4, 0, 0, 0, 8, 2, 16, 0]); peer.Barrier();
            That(fixture.Query("SELECT item_core FROM character_inventory_items WHERE item_uid=$p0", weaponUid) is [[var sortedCore]] && Equals(sortedCore, weaponCore),
                "个人仓库真实整理与重复整理保持完整物品核心及UID");
            Transfer(peer, 2, 0, weaponId, 1, 12, 7, 7, 1);
            That(peer.Expect(0, 14).Body[0] == 2 && peer.Expect(0, 14).Body[0] == 12, "cross cargo move clears personal and updates account");
            That(fixture.Query("SELECT character_id,item_core FROM character_inventory_items WHERE item_uid=$p0", weaponUid) is [[DBNull, var saved]] &&
                Equals(saved, weaponCore), "account storage changes owner while preserving complete core and UID");
            peer.Send(20, Build(w => { w.Write(8); w.Write(new byte[] { 8, 20, 16, 12, 24, 0, 32, 0 }); }));
            peer.Expect(1, 20, [1, 4, 0, 0, 0, 8, 12, 16, 0]);
            var clearSorted = peer.Expect(0, 1681).Body;
            That(clearSorted[0] == 12 && U16(clearSorted, 1) == 1 && U16(clearSorted, 3) == 0 && U32(clearSorted, 5) == uint.MaxValue,
                "账号仓库整理先用1681清除将重新载入的目标槽");
            var sorted = peer.Expect(0, 1681).Body;
            That(sorted[0] == 12 && U16(sorted, 1) == 2 && U16(sorted, 3) == 0 && U32(sorted, 5) == weaponId && U32(sorted, 128) == uint.MaxValue,
                "account sorting publishes new slot and clears previous slot");
            Transfer(peer, 0, 121, 3299, 17, 12, 1, 1, 17);
            That(peer.Expect(0, 14).Body[0] == 0 && peer.Expect(0, 14).Body[0] == 12, "partial deposit updates both stacks");
            Transfer(peer, 0, 121, 3299, 2, 12, 7, 0, 2, actualTargetSlot: 1);
            That(peer.Expect(0, 14).Body[0] == 0 && U32(peer.Expect(0, 14).Body, 9) == 19,
                "deposit into an empty requested slot merges into existing account stack");
            That(fixture.Query("SELECT COUNT(*) FROM character_inventory_slots WHERE list_type=12 AND slot_index=7") is [[0L]],
                "automatic merge does not allocate requested target slot seven");
            Transfer(peer, 12, 1, 1, 0, 0, 122, 0, 19, actualTargetSlot: 121);
            That(U32(peer.Expect(0, 14).Body, 9) == 600 && U32(peer.Expect(0, 14).Body, 5) == uint.MaxValue,
                "partial withdrawal merges full quantity and clears account stack");
            peer.Send(408, [65, 0]);
            peer.Expect(1, 408, [1]);
            That(U32(peer.Expect(0, 14).Body, 5) == uint.MaxValue, "personal upgrade consumes the requested tool");
            Cargo(peer.Expect(0, 13).Body, 2, 32, 0, 0);
            peer.Send(408, [65, 0]);
            peer.Expect(1, 408, [0, 17, 0]);
            peer.Barrier();
            peer.Send(713, [12, 0, 0]);
            peer.Expect(1, 713, [1, 12, 0, 0]);
            peer.Send(64, CargoPurchaseTcpTests.Purchase(100534));
            That(peer.Expect(1, 64).Body[0] == 1, "付费仓库档经真实商城商品购买");
            peer.Expect(0, 53, [1, 184, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            Cargo(peer.Expect(0, 13).Body, 12, 16, 750, 1);
            peer.Expect(1, 713, [1, 12, 0, 0]);
            peer.Send(306);
            peer.Expect(1, 306, [1]);
            That(U32(peer.Expect(0, 14).Body, 9) == 350, "material upgrade charges 250 from inventory");
            Cargo(peer.Expect(0, 13).Body, 12, 24, 750, 1);
            peer.Expect(1, 713, [1, 12, 0, 0]);
            peer.Barrier();
            That(fixture.Query("SELECT capacity,gold FROM account_cargo") is [[24L,750L]] &&
                fixture.Query("SELECT count(*) FROM character_inventory_slots WHERE list_type=12") is [[1L]],
                "材料扩容发布实际材料及容量，保留已有仓库物品、金币并恢复锁");
            Transfer(peer, 0, 121, 3299, 1, 12, 13, weaponId, 1);
            That(peer.Expect(0, 14).Body[0] == 0 && peer.Expect(0, 14).Body[0] == 12,
                "升级同连接立即存入仓库槽13，接受源Main槽13的装备编号回显");
            peer.Barrier();
            That(fixture.Query("SELECT json_extract(item_core,'$.ItemId'),json_extract(item_core,'$.Value') FROM character_inventory_items " +
                "JOIN character_inventory_slots USING(item_uid) WHERE list_type=12 AND slot_index=13") is [[3299L,1L]] &&
                fixture.Query("SELECT item_core FROM character_inventory_items WHERE item_uid=$p0", mirrorUid) is [[var mirrorCore]] && Equals(mirrorCore, weaponCore),
                "真正入库的是请求材料，不误移动或修改被回显的主背包装备及其完整属性");
            Transfer(peer, 12, 13, 13, 1, 0, 121, 3299, 1);
            That(U32(peer.Expect(0, 14).Body, 9) == 350 && U32(peer.Expect(0, 14).Body, 5) == uint.MaxValue,
                "回显存入后仍可取回并合堆，源仓库槽清空且数量守恒");
            peer.Barrier();
            peer.Send(7);
            peer.Expect(1, 7, [1]);
            peer.Expect(0, 2);
            var sibling = peer.Select(1, inventoryNotices: [(1, 713)]);
            Cargo(sibling[6].Body, 2, 8, 0, 0);
            Cargo(sibling[7].Body, 12, 24, 750, 1);
            Transfer(peer, 12, 0, weaponId, 1, 0, 3, 0, 1);
            That(peer.Expect(0, 14).Body[0] == 0 && U32(peer.Expect(0, 14).Body, 5) == uint.MaxValue,
                "sibling can withdraw shared equipment into quick bar");
            peer.Expect(1, 714, [1, 0, 3, 0]);
            peer.Send(308, Build(w => w.Write(750)));
            peer.Expect(1, 308, [1, 0, 0, 0, 0]);
            ExpectGold(peer, 750);
            peer.Barrier();
            That(fixture.Query("SELECT character_id,item_core FROM character_inventory_items WHERE item_uid=$p0", weaponUid) is [[long id, var core]] &&
                id == peer.Id && Equals(core, weaponCore), "sibling becomes sole owner of same UID and complete core");
        }
        using (var peer = Open(fixture))
        {
            var entry = peer.Select(1);
            That(U32(entry[4].Body, 376) == weaponId && U16(entry[4].Body, 374) == 3, "reconnect restores sibling equipment in quick bar");
            Cargo(entry[7].Body, 12, 24, 0, 0);
            peer.Barrier();
            Transfer(peer, 0, 3, weaponId, 1, 2, 7, 7, 1);
            peer.Expect(0, 14); peer.Expect(0, 14);
            SortPersonal(peer, weaponId);
        }
        using (var peer = Open(fixture))
        {
            var entry = peer.Select(1);
            Cargo(entry[6].Body, 2, 8, 0, 1);
            That(U16(entry[6].Body, 5) == 0 && U32(entry[6].Body, 7) == weaponId,
                "重登回放个人仓库整理后的槽0，不恢复旧槽7");
            using var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { });
            var restored = repo.LoadCharacter(owner, 1).Inventory.Find(new(InventoryListType.PersonalCargo, 0));
            That(restored?.Uid == weaponUid && JsonSerializer.Serialize(restored.Core) == weaponCore,
                "个人仓库整理后重登保持同一UID及完整属性");
            peer.Barrier();
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "warehouse transactions preserve database integrity");
    }

    static void SortPersonal(TcpPeer peer, int weaponId)
    {
        // 双端实收CMD20，长度前缀6，protobuf字段2=2、3=20、4=0。
        peer.Send(20, Convert.FromHexString("06000000100218142000"));
        peer.Expect(1, 20, [1, 4, 0, 0, 0, 8, 2, 16, 0]);
        var clearSorted = peer.Expect(0, 1681).Body;
        That(clearSorted[0] == 2 && U16(clearSorted, 1) == 1 && U16(clearSorted, 3) == 0 && U32(clearSorted, 5) == uint.MaxValue,
            "个人仓库整理先用1681清除将重新载入的目标槽");
        var sorted = peer.Expect(0, 1681).Body;
        That(sorted[0] == 2 && U16(sorted, 1) == 2 && U16(sorted, 3) == 0 && U32(sorted, 5) == weaponId &&
            U16(sorted, 126) == 7 && U32(sorted, 128) == uint.MaxValue,
            "个人仓库整理下发槽0完整物品并清空槽7");
        peer.Barrier();
    }

    static void Transfer(TcpPeer peer, byte source, short sourceSlot, int sourceId, int count, byte target, short targetSlot, int targetId, int moved, short? actualTargetSlot = null)
    {
        peer.Send(19, Build(w =>
        {
            w.Write(source); w.Write(sourceSlot); w.Write(sourceId); w.Write(count);
            w.Write(target); w.Write(targetSlot); w.Write(targetId); w.Write(0); w.Write(-1); w.Write((ushort)0);
        }));
        peer.Expect(1, 19, Build(w =>
        {
            w.Write((byte)1); w.Write(source); w.Write(sourceSlot); w.Write(moved); w.Write(target); w.Write(actualTargetSlot ?? targetSlot);
        }));
    }
    static void Cargo(byte[] body, byte list, ushort capacity, uint gold, ushort count)
    {
        int header = list == 12 ? 9 : 5;
        int tail = list == 2 ? 1 : 0;
        That(body[0] == list && U16(body, 1) == capacity && U16(body, header - 2) == count && body.Length == header + 123 * count + tail,
            "independent cargo capacity, count and complete entry widths");
        if (list == 12)
            That(U32(body, 3) == gold, "account cargo money precedes count");
        else
            That(body[header + 123 * count] == 0, "personal cargo auxiliary count follows all physical entries");
    }
    static void ExpectGold(TcpPeer peer, uint expected)
    {
        var delta = peer.Expect(0, 14).Body;
        That(delta.Length == 126 && delta[0] == 0 && U16(delta, 1) == 1 && U16(delta, 3) == 0 && U32(delta, 9) == expected,
            "gold delta is committed absolute balance");
    }
}
