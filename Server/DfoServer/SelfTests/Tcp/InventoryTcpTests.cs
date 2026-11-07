using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
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
        var clock = new Inventory.DailyBoosterTestClock(DateTimeOffset.UtcNow);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "inventory"), resources: resources, clock: clock);
        var characters = resources.Characters;
        var towns = resources.Towns;
        var owners = new AccountOwners();
        var repository = new CharacterRepository(fixture.Database);
        ItemTemplate stack, otherStack;
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var stacks = PvfScript.List(resources.Archive.ReadText("stackable/stackable.lst")).Keys.Where(id => id > 2)
                .Select(id => TryGet(id)).Where(item => item is { Kind: ItemKind.Consumable, StackLimit: >= 30,
                    IsPremiumService: false, HasAcquisitionEffect: false, Expiration: { AbsoluteTime: 0, Days: 0 } })
                .Take(2).ToArray();
            That(stacks.Length == 2, "背包移动夹具使用两种普通实体堆叠物品");
            stack = stacks[0]!;
            otherStack = stacks[1]!;
            var character = new GameSession(repository, towns, owner).CreateCharacter("BagStackTest", characters.Get(0));
            var inventory = new InventoryService(itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
            var grant = inventory.Grant(character, [new(stack.Id, 20), new(otherStack.Id, 3), new(0, 1234)]);
            That(grant.Accepted, "背包移动夹具的实体堆叠与金币整笔发放成功");
            character = InventoryService.Commit(repository, owner, character, grant);
            InventoryService.Commit(repository, owner, character, inventory.Move(character,
                new(new(InventoryListType.Main, 66), otherStack.Id, 0, new(InventoryListType.Main, 67), 0, 0, -1, 0, 0)));
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
            VerifyItemSpaceSynchronization(fixture, peer);
            That(entry[4].Body.Length == 2096 && U16(entry[4].Body, 3) == 17 && U16(entry[4].Body, 374) == 65 && U32(entry[4].Body, 380) == 20,
                "full inventory contains virtual balances and preseeded stacks");
            var stackUid = fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=65", peer.Id)[0][0];
            var split = MoveItem(0, 65, stack.Id, 7, 0, 66, 0);
            peer.Send(19, split);
            peer.Expect(1, 19, MoveAck(0, 65, 7, 0, 66));
            var full = peer.Expect(0, 13).Body;
            FullStack(full, 65, stack.Id, 13, 1234);
            FullStack(full, 66, stack.Id, 7, 1234);
            string before = Snapshot(fixture);
            peer.Send(19, split);
            peer.Expect(1, 19, [0, 23, 0, 0, 0]);
            peer.Barrier();
            That(Snapshot(fixture) == before, "replayed TCP split causes no duplicate core, audit or revision");
            peer.Send(19, MoveItem(0, 66, stack.Id, 0, 0, 65, stack.Id));
            peer.Expect(1, 19, MoveAck(0, 66, 7, 0, 65));
            Bytes(peer.Expect(0, 14).Body.AsSpan(0, 9), [0, 1, 0, 66, 0, 255, 255, 255, 255], "页内整堆合并只清空耗尽的源格，不重新标记目标物品");
            FullStack(peer.Expect(0, 13).Body, 65, stack.Id, 20, 1234);
            peer.Send(19, MoveItem(0, 65, stack.Id, 0, 0, 66, 0));
            peer.Expect(1, 19, MoveAck(0, 65, 20, 0, 66));
            peer.Barrier();
            peer.Send(19, MoveItem(0, 66, stack.Id, 0, 0, 67, otherStack.Id));
            peer.Expect(1, 19, MoveAck(0, 66, 1, 0, 67));
            peer.Barrier();
            That(fixture.Query("SELECT s.slot_index,json_extract(i.item_core,'$.ItemId'),json_extract(i.item_core,'$.Value') FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid) WHERE s.list_type=0 ORDER BY s.slot_index")
                is [[66L, long firstId, 3L], [67L, long secondId, 20L]] && firstId == otherStack.Id && secondId == stack.Id,
                "页内整堆移动及不同模板交换只有ACK，保留完整物品数量");
            peer.Send(19, MoveItem(0, 67, stack.Id, 0, 0, 66, otherStack.Id));
            peer.Expect(1, 19, MoveAck(0, 67, 1, 0, 66));
            peer.Barrier();
            peer.Send(19, MoveItem(0, 66, stack.Id, 0, 0, 65, 0));
            peer.Expect(1, 19, MoveAck(0, 66, 20, 0, 65));
            peer.Barrier();
            peer.Send(19, MoveItem(0, 65, stack.Id, 0, 0, 3, 0));
            peer.Expect(1, 19, MoveAck(0, 65, 20, 0, 3));
            peer.Barrier();
            That(fixture.Query("SELECT slot_index FROM character_inventory_slots WHERE list_type=0 ORDER BY slot_index") is [[3L], [67L]],
                "consumable moves into quick slot without moving the other stack");
            peer.Send(19, MoveItem(0, 3, stack.Id, 0, 0, 67, otherStack.Id));
            peer.Expect(1, 19, MoveAck(0, 3, 1, 0, 67));
            peer.Barrier();
            That(fixture.Query("SELECT s.slot_index,json_extract(i.item_core,'$.ItemId'),json_extract(i.item_core,'$.Value') FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid) WHERE s.list_type=0 ORDER BY s.slot_index")
                is [[3L, long quickId, 3L], [67L, long pageId, 20L]] && quickId == otherStack.Id && pageId == stack.Id,
                "背包与快捷栏交换只有ACK，不清槽补回物品，并保持各自数量");
            That(Equals(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE list_type=0 AND slot_index=67")[0][0], stackUid),
                "整堆移动和交换不重建实例UID");
            before = Snapshot(fixture);
            peer.Send(19, MoveItem(0, 67, stack.Id, 0, 0, 67, stack.Id));
            peer.Expect(1, 19, MoveAck(0, 67, 0, 0, 67));
            peer.Barrier();
            That(Snapshot(fixture) == before, "同格零变化只有ACK，不发送物品差量或写库");
            peer.Send(19, MoveItem(0, 65, 0, 5, 0, 67, stack.Id));
            peer.Expect(1, 19, MoveAck(0, 65, 5, 0, 67));
            full = peer.Expect(0, 13).Body;
            FullStack(full, 65, stack.Id, 5, 1234);
            FullStack(full, 67, stack.Id, 15, 1234);
            peer.Send(19, MoveItem(0, 65, stack.Id, 2, 0, 67, stack.Id));
            peer.Expect(1, 19, MoveAck(0, 65, 2, 0, 67));
            full = peer.Expect(0, 13).Body;
            FullStack(full, 65, stack.Id, 3, 1234);
            FullStack(full, 67, stack.Id, 17, 1234);
            peer.Send(19, MoveItem(0, 65, stack.Id, 0, 0, 67, stack.Id));
            peer.Expect(1, 19, MoveAck(0, 65, 3, 0, 67));
            Bytes(peer.Expect(0, 14).Body.AsSpan(0, 9), [0, 1, 0, 65, 0, 255, 255, 255, 255], "页内部分合并后耗尽源堆时只清源格");
            FullStack(peer.Expect(0, 13).Body, 67, stack.Id, 20, 1234);
            peer.Barrier();
            That(Equals(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE list_type=0 AND slot_index=67")[0][0], stackUid) &&
                fixture.Query("SELECT SUM(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=$p1", peer.Id, stack.Id) is [[20L]],
                "拆分合并保留目标UID及物品总量");
            peer.Send(19, MoveItem(0, 67, stack.Id, 7, 0, 4, 0));
            peer.Expect(1, 19, MoveAck(0, 67, 7, 0, 4));
            full = peer.Expect(0, 13).Body;
            FullStack(full, 4, stack.Id, 7, 1234);
            FullStack(full, 67, stack.Id, 13, 1234);
            peer.Send(19, MoveItem(0, 4, stack.Id, 2, 0, 67, stack.Id));
            peer.Expect(1, 19, MoveAck(0, 4, 2, 0, 67));
            full = peer.Expect(0, 13).Body;
            FullStack(full, 4, stack.Id, 5, 1234);
            FullStack(full, 67, stack.Id, 15, 1234);
            peer.Send(19, MoveItem(0, 4, stack.Id, 0, 0, 67, stack.Id));
            peer.Expect(1, 19, MoveAck(0, 4, 5, 0, 67));
            Bytes(peer.Expect(0, 14).Body.AsSpan(0, 9), [0, 1, 0, 4, 0, 255, 255, 255, 255], "快捷栏合并耗尽只发送无获得效果的清槽条目");
            full = peer.Expect(0, 13).Body;
            FullStack(full, 67, stack.Id, 20, 1234);
            That(!Enumerable.Range(0, U16(full, 3)).Any(index => U16(full, 5 + 123 * index) == 4), "完整载入不保留合并耗尽的快捷栏源堆");
            peer.Barrier();
            peer.Send(19, MoveItem(0, 3, otherStack.Id, 0, 0, 8, 0));
            peer.Expect(1, 19, MoveAck(0, 3, 3, 0, 8));
            peer.Barrier();
            That(fixture.Query("SELECT s.slot_index,json_extract(i.item_core,'$.Value') FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid) WHERE s.list_type=0 AND json_extract(i.item_core,'$.ItemId')=$p0", otherStack.Id) is [[8L, 3L]],
                "快捷栏格间整堆移动同样只有ACK，保持实际槽位和数量");
            peer.Send(19, MoveItem(0, 8, otherStack.Id, 1, 0, 7, 0));
            peer.Expect(1, 19, MoveAck(0, 8, 1, 0, 7));
            full = peer.Expect(0, 13).Body;
            FullStack(full, 8, otherStack.Id, 2, 1234);
            FullStack(full, 7, otherStack.Id, 1, 1234);
            peer.Send(19, MoveItem(0, 7, otherStack.Id, 0, 0, 8, otherStack.Id));
            peer.Expect(1, 19, MoveAck(0, 7, 1, 0, 8));
            Bytes(peer.Expect(0, 14).Body.AsSpan(0, 9), [0, 1, 0, 7, 0, 255, 255, 255, 255], "快捷栏格间合并只清除耗尽源格");
            FullStack(peer.Expect(0, 13).Body, 8, otherStack.Id, 3, 1234);
            peer.Send(7);
            peer.Expect(1, 7, [1]);
            peer.Expect(0, 2);
            peer.Create("BagGearTest");
            long staminaUntil = fixture.UtcNow.ToUnixTimeSeconds() + 600;
            fixture.Sql("UPDATE characters SET stamina_until=$p0 WHERE name=$p1", staminaUntil, "BagGearTest");
            var selected = peer.Select(1);
            That(selected[3].Body[36 + 84] == 10, "已持久化虚弱的选角Player直接携带10%，不先重装100%");
            That(selected[2].Body[133 + NameWidth("BagGearTest")] == 5 && selected[3].Body[126] == 5, "created character actor and player include five PVF equipment items");
            int quality = 999999998;
            ushort characterId = peer.Id;
            int weaponId = characters.Get(0).Equipment.Single(item => item.Slot == 12).Template.Id;
            int pantsId = characters.Get(0).Equipment.Single(item => item.Slot == 17).Template.Id;
            var quickUid = fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=3 AND slot_index=17", characterId)[0][0];
            // #819：入城五秒内的空格卸装必须真实提交，紧接着才能从该格穿回。
            peer.Send(19, MoveItem(0, 21, 0, 1, 3, 17, pantsId));
            peer.Expect(1, 19, MoveAck(0, 21, 1, 3, 17));
            DeltaAndAppearance(peer, 4, reloadMain: true);
            That(Equals(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=21", characterId)[0][0], quickUid),
                "入城窗口内卸装提交原UID，不仅回成功却仍保留穿戴");
            peer.Send(19, MoveItem(0, 21, pantsId, 1, 3, 17, 0));
            peer.Expect(1, 19, MoveAck(0, 21, 1, 3, 17));
            DeltaAndAppearance(peer, 5);
            peer.Barrier();
            string loginSnapshot = Snapshot(fixture);
            peer.Send(19, MoveItem(0, 21, pantsId, 1, 3, 17, 0));
            peer.Expect(1, 19, [0, 23, 0, 0, 3]);
            peer.Barrier();
            That(Snapshot(fixture) == loginSnapshot, "立即重穿后重放旧穿戴请求仍拒绝，不能反向卸装");
            clock.Advance(TimeSpan.FromSeconds(5));
            peer.Barrier();
            peer.Send(19, MoveItem(0, 21, 0, 1, 3, 17, pantsId));
            peer.Expect(1, 19, MoveAck(0, 21, 1, 3, 17));
            var weakRefresh = DeltaAndAppearance(peer, 4, reloadMain: true);
            That(weakRefresh.Player[36 + 84] == 10 &&
                fixture.Query("SELECT stamina_until FROM characters WHERE id=$p0", characterId) is [[long savedUntil]] && savedUntil == staminaUntil,
                "同epoch已通知10%后城镇脱装Actor/Player保持虚弱及原期限");
            var quickRequest = Convert.FromHexString("0003000000000000000000001500E047000000000000FFFFFFFF0000");
            peer.Send(19, quickRequest);
            peer.Expect(1, 19, Convert.FromHexString("0100030001000000001500"));
            peer.Barrier();
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
            clock.Advance(TimeSpan.FromSeconds(5));
            peer.Barrier();
            That(reselected[3].Body[126] == 4 && reselected[4].Body.Length == 1973 && U16(reselected[4].Body, 374) == 3 &&
                U32(reselected[4].Body, 376) == 18400, "reselect restores equipment in quick slot through full inventory");
            peer.Send(19, MoveItem(0, 3, pantsId, 1, 3, 17, 0));
            peer.Expect(1, 19, MoveAck(0, 3, 1, 3, 17));
            weakRefresh = DeltaAndAppearance(peer, 5);
            That(weakRefresh.Player[36 + 84] == 10 &&
                fixture.Query("SELECT stamina_until FROM characters WHERE id=$p0", characterId) is [[long refreshedUntil]] && refreshedUntil == staminaUntil,
                "同档反复穿装刷新仍保留10%，不依赖下一NOTI4档位或修改期限");
            peer.Send(19, MoveItem(0, 8, 0, 1, 3, 17, pantsId));
            peer.Expect(1, 19, MoveAck(0, 8, 1, 3, 17));
            DeltaAndAppearance(peer, 4, reloadMain: true);
            peer.Send(19, MoveItem(0, 8, pantsId, 0, 0, 3, 0));
            peer.Expect(1, 19, MoveAck(0, 8, 1, 0, 3));
            peer.Barrier();
            That(Equals(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=3", characterId)[0][0], quickUid),
                "装备快捷栏首尾换格只有ACK，并保留原实例UID");
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
            DeltaAndAppearance(peer, 4, reloadMain: true);
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
            DeltaAndAppearance(peer, 4, reloadMain: true);
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
            peer.Send(19, MoveItem(0, 10, 0, 1, 3, 12, weaponId));
            peer.Expect(1, 19, MoveAck(0, 10, 1, 3, 12));
            DeltaAndAppearance(peer, 4, reloadMain: true);
            peer.Send(19, MoveItem(0, 12, 0, 1, 3, 17, pantsId));
            peer.Expect(1, 19, MoveAck(0, 12, 1, 3, 17));
            DeltaAndAppearance(peer, 3, reloadMain: true);
            peer.Send(20, Convert.FromHexString("06000000100018012000"));
            peer.Expect(1, 20, Convert.FromHexString("010400000008001000"));
            var slotCleared = peer.Expect(0, 1681).Body;
            var slotSorted = peer.Expect(0, 1681).Body;
            That(slotCleared.Length == 249 && U16(slotCleared, 3) == 9 && U32(slotCleared, 5) == uint.MaxValue &&
                U16(slotCleared, 126) == 10 && U32(slotCleared, 128) == uint.MaxValue,
                "按栏位整理先清空目标非空槽，保留重新载入图标effect");
            That(slotSorted.Length == 372 && U16(slotSorted, 3) == 9 && U32(slotSorted, 5) == 101010912 &&
                U16(slotSorted, 126) == 10 && U32(slotSorted, 128) == 18400 &&
                U16(slotSorted, 249) == 12 && U32(slotSorted, 251) == uint.MaxValue,
                "装备按栏位整理(condition=0)保持原槽位先后向前紧凑填空，不按ItemId重排");
            string afterSlotSort = Snapshot(fixture);
            peer.Send(20, Convert.FromHexString("06000000100018012000"));
            peer.Expect(1, 20, Convert.FromHexString("010400000008001000"));
            peer.Barrier();
            That(Snapshot(fixture) == afterSlotSort, "已紧凑的装备页重复按栏位整理为零变化，仅回ACK");
            peer.Send(20, Convert.FromHexString("06000000100018012001"));
            peer.Expect(1, 20, Convert.FromHexString("010400000008001000"));
            var cleared = peer.Expect(0, 1681).Body;
            var sorted = peer.Expect(0, 1681).Body;
            That(cleared.Length == 249 && U32(cleared, 5) == uint.MaxValue && U32(cleared, 128) == uint.MaxValue,
                "整理先用无播报差量清槽，后续重新载入仍可标记获得图标effect");
            That(U32(sorted, 5) == 18400 && U32(sorted, 128) == 101010912,
                "装备按品级整理(condition=1)使用1681保持完整对象和排序，不走获得播报或13全量载入");
            byte[] discard = DeleteItem(9, quality);
            peer.Send(18, discard);
            peer.Expect(1, 18, Convert.FromHexString("010C000000080012060809100118001800"));
            var removed = peer.Expect(0, 14).Body;
            That(U16(removed, 3) == 9 && U32(removed, 5) == uint.MaxValue, "protobuf discard clears exact main slot");
            beforeReplay = Snapshot(fixture);
            peer.Send(18, discard);
            peer.Expect(1, 18, Convert.FromHexString("000400000008001801"));
            peer.Barrier();
            That(Snapshot(fixture) == beforeReplay, "replayed discard does not mutate storage");
            peer.Send(19, MoveItem(0, 8, 0, 0, 0, 10, weaponId));
            peer.Expect(1, 19, MoveAck(0, 8, 1, 0, 10));
            peer.Barrier();
        }
        await fixture.StopAsync();
        await fixture.StartAsync("reopened", isolatedProcess: true);
        using (var peer = Open(fixture))
        {
            var packets = peer.Select(1);
            That(packets[3].Body[126] == 3 && packets[4].Body.Length == 1973 && U16(packets[4].Body, 374) == 8 && U32(packets[4].Body, 376) == 101010912, "restart restores quick slot equipment without starter regrant");
        }
        That(fixture.Query("SELECT count(*) FROM character_inventory_items WHERE character_id=2") is [[4L]], "deleted equipment never returns on restart");
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]], "TCP inventory database integrity");
        await fixture.StopAsync();
        await VerifyRulesAsync(source, Path.Combine(root, "inventory-rules"), characters, towns, stack, resources);
        await VerifyLocksAsync(source, Path.Combine(root, "inventory-locks"), characters, towns, resources);
        await VerifyRentalAsync(source, Path.Combine(root, "inventory-rental"), resources);
        Console.WriteLine("PASS C# TCP inventory: full list, split, merge, quick slot, equip, sort, discard, replay and restart");
    }

    static async Task VerifyRulesAsync(ServerOptions source, string root, CharacterCatalog characters, TownCatalog towns, ItemTemplate stack, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, root, resources: resources);
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
            Bytes(entry[8].Body, [1, 0, 65, 0], "sorting lock restored after item lists");
            Bytes(entry[9].Body, [1, 0, 0, 20, 0, 1], "only unexpired equipment lock restored");
            That(U16(entry[4].Body, 3) == 18 && fixture.Query("SELECT count(*) FROM inventory_audit_log WHERE action_name='item_expired'") is [[1L]],
                "selection expires locked item before inventory projection");
            string before = Snapshot(fixture);
            peer.Send(19, MoveItem(0, 65, stack.Id, 5, 0, 66, stack.Id));
            peer.Expect(1, 19, [0, 23, 0, 0, 0]);
            peer.Barrier();
            That(Snapshot(fixture) == before, "insufficient merge space preserves all database rows and revision");
            peer.Send(19, MoveItem(0, 65, stack.Id, 2, 0, 66, stack.Id));
            peer.Expect(1, 19, MoveAck(0, 65, 2, 0, 66));
            var merged = peer.Expect(0, 13).Body;
            FullStack(merged, 65, stack.Id, 18);
            FullStack(merged, 66, stack.Id, stack.StackLimit);
            int lockedOffset = Enumerable.Range(0, U16(merged, 3)).Select(index => 5 + 123 * index).Single(index => U16(merged, index) == 65);
            That(merged[lockedOffset + 95] == 1, "页内合并完整载入保留源堆排序锁字段");
            peer.Expect(1, 713, [1, 0, 65, 0]);
            peer.Expect(0, 251, [1, 0, 0, 20, 0, 1]);
            peer.Send(19, MoveItem(0, 65, stack.Id, 0, 0, 4, 0));
            peer.Expect(1, 19, MoveAck(0, 65, 18, 0, 4));
            peer.Expect(1, 714, [1, 0, 4, 0]);
            That(fixture.Query("SELECT json_extract(i.item_core,'$.SortLockFlag') FROM character_inventory_items i JOIN character_inventory_slots s USING(item_uid) WHERE s.list_type=0 AND s.slot_index=4") is [[0L]],
                "无动画整堆移动仍清除排序锁并同步专属锁通知");
            peer.Send(19, MoveItem(0, 4, stack.Id, 3, 0, 65, 0));
            peer.Expect(1, 19, MoveAck(0, 4, 3, 0, 65));
            var full = peer.Expect(0, 13).Body;
            FullStack(full, 4, stack.Id, 15);
            FullStack(full, 65, stack.Id, 3);
            peer.Expect(0, 251, [1, 0, 0, 20, 0, 1]);
            peer.Send(19, MoveItem(0, 65, stack.Id, 0, 0, 4, stack.Id));
            peer.Expect(1, 19, MoveAck(0, 65, 3, 0, 4));
            Bytes(peer.Expect(0, 14).Body.AsSpan(0, 9), [0, 1, 0, 65, 0, 255, 255, 255, 255], "快捷栏合并只清除真正耗尽的源格");
            FullStack(peer.Expect(0, 13).Body, 4, stack.Id, 18);
            peer.Expect(0, 251, [1, 0, 0, 20, 0, 1]);
            peer.Send(20, Convert.FromHexString("0900000008141000180120FF01"));
            peer.Expect(1, 20, Convert.FromHexString("010400000008001000"));
            var clearSorted = peer.Expect(0, 1681).Body;
            That(clearSorted.Length == 126 && U16(clearSorted, 3) == 9 && U32(clearSorted, 5) == uint.MaxValue,
                "整理迁入原空槽的装备也先清客户端槽对象，保留重新载入effect");
            var sorted = peer.Expect(0, 1681).Body;
            That(U16(sorted, 1) == 2 && U16(sorted, 3) == 9 && sorted[99] == 7 && U32(sorted, 128) == uint.MaxValue,
                "fallback sort moves equipment safety lock without reviving expired item");
            peer.Expect(0, 251, [1, 0, 0, 9, 0, 1]);
            before = Snapshot(fixture);
            peer.Send(18, DeleteItem(9, InventoryRewardGrantService.InitialQuality));
            peer.Expect(1, 18, Convert.FromHexString("000400000008001801"));
            peer.Barrier();
            That(Snapshot(fixture) == before, "sorting does not release equipment deletion protection");
            peer.Send(18, DeleteItem(4, 18, 0));
            peer.Expect(1, 18, Convert.FromHexString("010C000000080012060804101218001800"));
            peer.Expect(0, 14);
            peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_inventory_items") is [[5L]], "discard zero removes entire stack and expired item stays removed");
            peer.Send(18, DeleteItem(16, InventoryRewardGrantService.InitialQuality, 0, 3));
            peer.Expect(1, 18, Convert.FromHexString("010C000000080312060810100118001800"));
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
        await fixture.StopAsync();
        var broom = resources.ItemResources.Items.Get(106040040);
        object broomUid;
        int broomSkillCount;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var profile = characters.Get(3);
            var learning = resources.Learning;
            var role = repository.Create(owner, new NewCharacter("BroomTest", towns.Room, towns.InitialReturn)
            {
                Job = 3, AcceptedStoryLevel = 1, Skills = profile.Skills,
                InitialLearning = SkillLearning.Initialize(3, profile.Skills, learning).State!,
                Inventory = InventoryRewardGrantService.CreateStarter(profile)
            });
            var reset = new CharacterProgressReset(characters, learning, SelfTestCatalogs.Growth(resources.Archive, learning));
            role = repository.ResetProgress(owner, reset.Prepare(role, new(CharacterProgressField.Level, owner.Account, role.Name, 15, 0)));
            role = repository.ResetProgress(owner, reset.Prepare(role, new(CharacterProgressField.Grow, owner.Account, role.Name, 5, 0)));
            broomSkillCount = role.Learning.Trees.Sum(tree => tree.Skills.Count);
            That(role.Skills.Any(skill => skill.Id == 368 && skill.Level > 0) && role.Skills.All(skill => skill.Id != 91),
                "real enchantress grants black magic broom without inventing witch mastery");
            var richBroom = InventoryRewardGrantService.Create(broom) with
            {
                Attr = 11, Durability = 17, EnchantCardId = 10309006, EnchantUpgrade = 2,
                AmplifyType = 2, AmplifyValue = 21, GenuineUpgrade = 4, Rune = 8,
                Random0 = new(1, 2, 3), Random1 = new(2, 4, 5), Random2 = new(3, 6, 7),
                RandomState = 1, RandomChangedIndex = 1, RandomChangeState = 1, RandomChange = new(4, 8, 9)
            };
            repository.CommitInventory(owner, role, new(role.Inventory, role.Inventory with
            {
                Items = [.. role.Inventory.Items,
                    new(0, new(InventoryListType.Main, 9), richBroom),
                    new(0, new(InventoryListType.Main, 11), richBroom with
                    { ItemId = profile.Equipment.Single(item => item.Slot == 12).Template.Id, Attr = 6, EnchantUpgrade = 3, GenuineUpgrade = 5 }),
                    new(0, new(InventoryListType.Main, 12), richBroom with
                    { Attr = 15, Durability = 13, AmplifyValue = 25, GenuineUpgrade = 7, RandomChange = new(5, 9, 10) })
                ]
            }, "fixture_enchantress_broom"));
            broomUid = fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=9", role.Id)[0][0]!;
        }
        await fixture.StartAsync("broom");
        byte[] originalBroom;
        using (var peer = Open(fixture))
        {
            var entry = peer.Select(1);
            var full = entry[4].Body;
            int at = Enumerable.Range(0, U16(full, 3)).Select(index => 5 + index * 123).Single(index => U16(full, index) == 9);
            originalBroom = full.AsSpan(at, 123).ToArray();
            That(originalBroom[10] == 11 && U16(originalBroom, 11) == 17 && U32(originalBroom, 14) == 10309006 &&
                originalBroom[18] == 2 && originalBroom[19] == 2 && U16(originalBroom, 20) == 21 &&
                U16(originalBroom, 69) == 8 && originalBroom[87] == 4 &&
                originalBroom.AsSpan(71, 16).SequenceEqual(new byte[] { 3, 1, 2, 3, 2, 4, 6, 3, 5, 7, 1, 1, 1, 4, 8, 9 }),
                "真实入城完整条目保留强化、附魔、增幅、锻造、符文及随机属性/变换预览");
            int previousWeapon = characters.Get(3).Equipment.Single(item => item.Slot == 12).Template.Id;
            foreach (var target in new[] { (Slot: (short)11, Id: previousWeapon, Upgrade: (byte)6), (Slot: (short)12, Id: broom.Id, Upgrade: (byte)15) })
            {
                var originalTarget = BoosterSelectionTcpTests.EquipmentEntry(full, target.Slot, true);
                That(U32(originalTarget, 2) == target.Id && originalTarget[10] == target.Upgrade &&
                    !originalTarget.AsSpan(2).SequenceEqual(originalBroom.AsSpan(2)),
                    "交换目标使用独立非零属性实例，同模板也不能按模板编号代替实例");
                var targetUid = fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=$p1", peer.Id, target.Slot)[0][0];
                string cores = fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", peer.Id);
                peer.Send(19, MoveItem(0, 9, broom.Id, 1, 0, target.Slot, target.Id));
                peer.Expect(1, 19, MoveAck(0, 9, 1, 0, target.Slot));
                peer.Barrier();
                That(fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", peer.Id) == cores &&
                    Equals(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=9", peer.Id)[0][0], targetUid) &&
                    Equals(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=$p1", peer.Id, target.Slot)[0][0], broomUid),
                    "物品栏内交换只发ACK，不清槽或增量载入，保留两件装备的原UID和全部核心属性");
                peer.Send(7);
                peer.Expect(1, 7, [1]);
                peer.Expect(0, 2);
                full = peer.Select(1)[4].Body;
                Bytes(BoosterSelectionTcpTests.EquipmentEntry(full, 9, true).AsSpan(2), originalTarget.AsSpan(2),
                    "重选后的目标装备121B完整内容仅随交换改变槽号，不丢强化、附魔或随机属性");
                Bytes(BoosterSelectionTcpTests.EquipmentEntry(full, target.Slot, true).AsSpan(2), originalBroom.AsSpan(2),
                    "重选后的原装备全部非槽位字节保留，同模板交换也不串用另一实例的属性");
                peer.Send(19, MoveItem(0, 9, target.Id, 1, 0, target.Slot, broom.Id));
                peer.Expect(1, 19, MoveAck(0, 9, 1, 0, target.Slot));
                peer.Barrier();
                That(fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", peer.Id) == cores &&
                    Equals(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=9", peer.Id)[0][0], broomUid) &&
                    Equals(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=$p1", peer.Id, target.Slot)[0][0], targetUid),
                    "交换回原槽仍无图标重载或属性变更，原UID恢复且不重新发放装备");
            }
            var previousInstance = fixture.Query("SELECT i.item_uid,i.item_core FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid) WHERE s.character_id=$p0 AND s.list_type=3 AND s.slot_index=12", peer.Id).Single();
            var wear = MoveItem(0, 9, broom.Id, 1, 3, 12, previousWeapon);
            peer.Send(19, wear);
            peer.Expect(1, 19, MoveAck(0, 9, 1, 3, 12));
            var cleared = peer.Expect(0, 1681).Body;
            That(cleared[0] == 0 && cleared.Length == 126 && U16(cleared, 1) == 1 && U16(cleared, 3) == 9 && U32(cleared, 5) == uint.MaxValue,
                "普通换装仍清除旧模板并写回原装备，图标路径不变但不发布获得通知");
            var update = DeltaAndAppearance(peer, 5, "BroomTest", broomSkillCount, extraEquipmentBytes: 15);
            var returnedWeapon = update.Main;
            int appearance = 134 + NameWidth("BroomTest");
            That(update.Actor[appearance] == 12 && U32(update.Actor, appearance + 1) == broom.Id &&
                update.Actor[appearance + 13] == 23 && update.Actor[appearance + 14] == 4 &&
                U32(update.Actor, appearance + 20) == 10309006 && update.Actor[appearance + 24] == 2,
                "穿戴后的Actor仍刷新原装备的强化/增幅标记、锻造和附魔信息");
            CheckBroomPlayer(update.Player);
            That(U16(returnedWeapon, 3) == 9 && U32(returnedWeapon, 5) == previousWeapon,
                "无获得通知的完整条目仍显示实际返回背包的武器");
            peer.Barrier();
            That(Equals(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=3 AND slot_index=12", peer.Id)[0][0], broomUid),
                "enchantress broom swap commits original UID before successful reply");
            That(fixture.Query("SELECT i.item_uid,i.item_core FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid) WHERE s.character_id=$p0 AND s.list_type=0 AND s.slot_index=9", peer.Id).Single().SequenceEqual(previousInstance),
                "普通交换保留返回装备的原UID及完整核心，不重新发放实例");
            string before = Snapshot(fixture);
            peer.Send(19, wear);
            peer.Expect(1, 19, [0, 23, 0, 0, 3]);
            peer.Barrier();
            That(Snapshot(fixture) == before, "repeated broom swap preserves committed inventory");
        }
        using (var peer = Open(fixture))
        {
            CheckBroomPlayer(peer.Select(1)[3].Body);
            peer.Send(19, MoveItem(3, 12, broom.Id, 1, 0, 10, 0));
            peer.Expect(1, 19, MoveAck(3, 12, 1, 0, 10));
            var update = DeltaAndAppearance(peer, 4, "BroomTest", broomSkillCount, reloadMain: true);
            var row = update.Main.AsSpan(3, 123);
            That(U16(update.Main, 3) == 10 && row.Slice(2, 11).SequenceEqual(originalBroom.AsSpan(2, 11)) &&
                row[13] == 0 && row[13] <= originalBroom[13] && row[14..].SequenceEqual(originalBroom.AsSpan(14)),
                "脱下后的1681完整核心逐字节保留，只有目标槽和合法穿戴解封标志可变");
            peer.Barrier();
            That(Equals(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=10", peer.Id)[0][0], broomUid),
                "reconnected enchantress restores and unequips the same broom instance");
        }
        using (var peer = Open(fixture))
        {
            var full = peer.Select(1)[4].Body;
            int at = Enumerable.Range(0, U16(full, 3)).Select(index => 5 + index * 123).Single(index => U16(full, index) == 10);
            var row = full.AsSpan(at, 123);
            That(row.Slice(2, 11).SequenceEqual(originalBroom.AsSpan(2, 11)) && row[14..].SequenceEqual(originalBroom.AsSpan(14)),
                "脱下重登的123B条目仍保留原强化、附魔、随机属性和全部扩展");
            peer.Barrier();
        }

        void CheckBroomPlayer(byte[] player)
        {
            const int at = 127;
            That(player[at] == 12 && U32(player, at + 1) == broom.Id && player[at + 9] == 11 && U16(player, at + 10) == 17 &&
                U32(player, at + 18) == 10309006 && player[at + 22] == 2 && player[at + 23] == 2 && U16(player, at + 24) == 21 &&
                U16(player, at + 32) == 8 && player[at + 50] == 4 &&
                player.AsSpan(at + 34, 16).SequenceEqual(new byte[] { 3, 1, 2, 3, 2, 4, 5, 3, 6, 7, 1, 1, 1, 4, 8, 9 }),
                "完整Player独立消费强化、附魔、增幅、锻造、符文和交错随机属性，包含变换预览");
        }
    }

    static string Snapshot(TcpTestFixture fixture) => JsonSerializer.Serialize(new
    {
        Characters = fixture.Query("SELECT * FROM characters ORDER BY id"),
        Items = fixture.Query("SELECT * FROM character_inventory_items ORDER BY item_uid"),
        Slots = fixture.Query("SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index"),
        Audit = fixture.Query("SELECT * FROM inventory_audit_log ORDER BY audit_id")
    });
    static (byte[] Main, byte[] Actor, byte[] Player) DeltaAndAppearance(TcpPeer peer, byte count, string name = "BagGearTest", int totalSkills = 12,
        int extraEquipmentBytes = 0, bool reloadMain = false)
    {
        var cleared = reloadMain ? peer.Expect(0, 1681).Body : null;
        var main = peer.Expect(0, 1681).Body;
        That(main[0] == 0 && U16(main, 1) == 1 && main.Length == 126,
            "普通穿脱仍同步完整Main条目，只使用无获得通知入口");
        if (cleared is not null)
            That(cleared.Length == 126 && cleared[0] == 0 && U16(cleared, 1) == 1 &&
                U16(cleared, 3) == U16(main, 3) && U32(cleared, 5) == uint.MaxValue && U32(main, 5) != uint.MaxValue,
                "脱到背包或快捷栏空格先静默清除ACK对象，再完整装载以触发图标effect");
        var actor = peer.Expect(0, 2).Body;
        var player = peer.Expect(0, 2).Body;
        That(actor[133 + NameWidth(name)] == count && player[126] == count && player.Length == 167 + count * 88 + totalSkills * 3 + extraEquipmentBytes,
            "appearance and complete player skill tail agree after wear change");
        return (main, actor, player);
    }
    static void FullStack(byte[] body, short slot, int itemId, int count, uint gold = 0)
    {
        That(body[0] == 0 && U16(body, 1) == 24 && body.Length == 5 + 123 * U16(body, 3), "Main内数量变动使用完整载入头和条目");
        int offset = Enumerable.Range(0, U16(body, 3)).Select(index => 5 + 123 * index).Single(index => U16(body, index) == slot);
        That(U32(body, offset + 2) == itemId && U32(body, offset + 6) == count, "无图标获得动画载入保留实际模板与绝对数量");
        That(U16(body, 5) == 0 && U32(body, 11) == gold, "数量同步不改金币钱包或伪造金币获得");
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
