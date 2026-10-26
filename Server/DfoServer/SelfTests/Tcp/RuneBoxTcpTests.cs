using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

// CMD27 的 [rune random box] 分支：源物品在主背包，产物按族进入容器46(护石0..69、符文70..209)。
// 回包合同：130B ACK27 → 主背包全量 NOTI13 → 容器46全量 NOTI13。
internal static class RuneBoxTcpTests
{
    const int RuneBox = 10310784, TalismanBox = 490014161, LastBox = 10310786;

    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        LotteryTests.RunRuneBoxes(resources);
        var items = resources.ItemResources;
        var catalog = items.Items;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "rune-box"), gm: true, resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        ushort character;
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var role = repo.Create(owner, new("RuneBox", resources.Towns.Room, resources.Towns.InitialReturn)
                { Job = 0, Level = 95, GrowType = 1, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills });
            character = role.Id;
            var service = new InventoryService(items.Items, items.GoldLimits, items.EpicPieces);
            var granted = service.Grant(role, [new(RuneBox, 2), new(TalismanBox, 1), new(LastBox, 1)]);
            That(granted.Accepted, "符文箱 TCP 夹具奖励全部进入库存");
            var saved = InventoryService.Commit(repo, owner, role, granted);
            repo.CommitInventory(owner, saved, new(saved.Inventory, saved.Inventory with
            {
                Items = saved.Inventory.Items.Add(new(0, new(InventoryListType.Main, 121),
                    new ItemCore(RuneBox, ItemKind.Material, 1)))
            }, "rune_box_invalid_source_fixture"));
        }
        await fixture.StartAsync();
        int stoneProduct = 0;
        short stoneDestination = -1;
        using (var peer = Open(fixture))
        {
            peer.Select();
            var runeBox = SlotOf(RuneBox);
            var talisman = SlotOf(TalismanBox);
            var last = SlotOf(LastBox);
            That(runeBox.List == 0 && talisman.List == 0 && last.List == 0, "三个符文箱都落在主背包消耗品栏");

            string invalidBaseline = Snapshot();
            peer.Send(27, Request(121)); peer.Expect(1, 27, [0, 19, 0]); peer.Barrier();
            That(Snapshot() == invalidBaseline, "符文盒源实例种类与真实模板不符时拒绝，库存、UID、revision与审计均不变");

            var first = OpenBox(peer, runeBox.Slot);
            var firstRune = catalog.Rune(first.Product)!;
            // 0/0 箱只有本职业骰命中时才限定本职业；随机结果必须落在声明品级的真实符文池内。
            That(catalog.RuneRandomChoices().TryGet(0, 0, out var runeRow) &&
                runeRow.Grades.Any(entry => entry.Grade == firstRune.Grade) &&
                catalog.RuneCandidates(firstRune.Grade).Contains(first.Product) && first.Destination is >= 70 and < 210,
                "符文箱产物落在声明品级池并进入符文页 product=" + first.Product + " grade=" + firstRune.Grade +
                " dest=" + first.Destination);
            var main = peer.Expect(0, 13).Body;
            var remaining = MainRecords(main).Where(record => record.Slot == runeBox.Slot).ToArray();
            That(main[0] == 0 && remaining.Length == 1 && remaining[0].Id == RuneBox && remaining[0].Value == 1,
                "开箱后主背包全量携带源格剩余一件");
            var page = peer.Expect(0, 13).Body;
            That(page[0] == 46 && PageRecords(page).Any(record => record.Slot == first.Destination && record.Id == first.Product),
                "护石符文页全量携带新产物");

            var second = OpenBox(peer, runeBox.Slot);
            main = peer.Expect(0, 13).Body;
            That(MainRecords(main).All(record => record.Slot != runeBox.Slot), "第二只开启后源格从主背包全量中消失");
            page = peer.Expect(0, 13).Body;
            That(PageRecords(page).Any(record => record.Slot == second.Destination && record.Id == second.Product),
                "第二只产物同样进入护石符文页");

            var stone = OpenBox(peer, talisman.Slot);
            stoneProduct = stone.Product;
            stoneDestination = stone.Destination;
            var stoneDefinition = catalog.Talisman(stone.Product)!;
            That(!stoneDefinition.IsPlaceholder && stoneDefinition.AllowsUse(95, 0, 1) &&
                catalog.Get(stone.Product).Rarity == 2 && stone.Destination is >= 0 and < 70,
                "护石箱产物为2级本职业可用护石并进入护石页槽位0..69");
            main = peer.Expect(0, 13).Body;
            page = peer.Expect(0, 13).Body;
            That(MainRecords(main).All(record => record.Slot != talisman.Slot) &&
                PageRecords(page).Any(record => record.Slot == stone.Destination && record.Id == stone.Product &&
                    record.Value == InventoryRewardGrantService.InitialQuality),
                "护石产物以实例品级值进入容器46全量");
            peer.Barrier();
            That(Rows("json_extract(i.item_core,'$.ItemId')=" + stone.Product + " AND s.list_type=46 AND s.slot_index=" +
                stone.Destination) == 1, "护石产物已提交到容器46对应槽位");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            short slot = SlotOf(LastBox).Slot;
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_rune_box BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'rune box rollback'); END");
            peer.Send(27, Request(slot));
            peer.Closed();
            That(before == Snapshot(), "晚期SQL失败不回成功并原子回滚源、产物、审计和UID");
            fixture.Sql("DROP TRIGGER fail_rune_box");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            short slot = SlotOf(LastBox).Slot;
            OpenBox(peer, slot);
            peer.Expect(0, 13);
            peer.Expect(0, 13);
            peer.Barrier();
        }
        string committed = Snapshot();
        await fixture.StopAsync();
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Barrier();
            That(committed == Snapshot(), "重启后源扣减、护石页产物与UID保持提交结果");
            var body = peer.SpecialContainers[4].Body;
            That(body[0] == 46 && PageRecords(body).Any(record => record.Slot == stoneDestination && record.Id == stoneProduct),
                "重启后容器46仍携带护石产物");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "符文箱数据库完整性");

        (short List, short Slot) SlotOf(int itemId)
        {
            var row = fixture.Query(
                "SELECT s.list_type,s.slot_index FROM character_inventory_slots s JOIN character_inventory_items i ON i.item_uid=s.item_uid " +
                "WHERE s.character_id=$p0 AND json_extract(i.item_core,'$.ItemId')=$p1 ORDER BY s.list_type,s.slot_index LIMIT 1",
                character, itemId)[0];
            return (Convert.ToInt16(row[0]), Convert.ToInt16(row[1]));
        }
        long Rows(string where) => Convert.ToInt64(fixture.Query(
            "SELECT COUNT(*) FROM character_inventory_slots s JOIN character_inventory_items i ON i.item_uid=s.item_uid " +
            "WHERE s.character_id=$p0 AND " + where, character)[0][0]);
        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT revision,gold FROM characters")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'"));
    }

    static (int Product, short Destination, byte[] Ack) OpenBox(TcpPeer peer, short slot)
    {
        peer.Send(27, Request(slot));
        byte[] ack = peer.Expect(1, 27).Body;
        That(ack.Length == 130 && ack[0] == 1 && U16(ack, 1) == (ushort)slot && U32(ack, 126) == 0,
            "符文箱ACK27按130B契约回执 len=" + ack.Length + " hex=" +
            Convert.ToHexString(ack.AsSpan(0, Math.Min(8, ack.Length))));
        int product = (int)U32(ack, 5);
        uint count = U32(ack, 9);
        That(count == 1 || count == (uint)InventoryRewardGrantService.InitialQuality,
            "ACK普通记录数量字段为堆叠增量或护石实例品级值");
        return (product, (short)U16(ack, 3), ack);
    }

    static byte[] Request(short slot) => Build(writer =>
    {
        writer.Write((ushort)0);
        writer.Write(slot);
    });

    // Main全量：[0]容器、[1..2]扩展格数、[3..4]条数、每条123B自偏移5开始。
    static IEnumerable<(short Slot, int Id, int Value)> MainRecords(byte[] body) =>
        Enumerable.Range(0, U16(body, 3)).Select(index => 5 + index * 123)
            .Select(at => ((short)U16(body, at), (int)U32(body, at + 2), (int)U32(body, at + 6)));

    // 容器46全量：[0]容器、[1..2]条数、每条123B自偏移3开始。
    static IEnumerable<(short Slot, int Id, int Value)> PageRecords(byte[] body) =>
        Enumerable.Range(0, U16(body, 1)).Select(index => 3 + index * 123)
            .Select(at => ((short)U16(body, at), (int)U32(body, at + 2), (int)U32(body, at + 6)));
}
