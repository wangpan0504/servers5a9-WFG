using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using PvfLib;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class TalismanTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var catalog = resources.ItemResources.Items;
        var costs = catalog.TalismanRefineCosts();
        var (stone, legacyStone, campRunes, legacyRune) = Pick(catalog, resources.Archive, costs);
        int perRefine = costs.CountsByRarity[catalog.Get(stone).Rarity];

        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "talisman"), resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        ushort character;
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var role = repo.Create(owner, new("Talisman", resources.Towns.Room, resources.Towns.InitialReturn)
                { Job = 0, Level = 95, GrowType = 1, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills });
            character = role.Id;
            var service = new InventoryService(resources.ItemResources.Items, resources.ItemResources.GoldLimits,
                resources.ItemResources.EpicPieces);
            var granted = service.Grant(role, [new(stone, 1), new(legacyStone, 1),
                ..campRunes.Select(id => new InventoryReward(id, 3)), new(legacyRune, 2),
                new(costs.MaterialId, checked(perRefine * 3))]);
            That(granted.Accepted, "护石 TCP 夹具奖励全部进入库存");
            InventoryService.Commit(repo, owner, role, granted);
        }
        // 第二枚护石与第二枚符文改写成旧存档分类：普通背包格上的 Equipment/Consumable。
        long legacyStoneUid = Uid(legacyStone), legacyRuneUid = Uid(legacyRune);
        fixture.Sql("UPDATE character_inventory_slots SET list_type=0,slot_index=9 WHERE item_uid=$p0", legacyStoneUid);
        fixture.Sql("UPDATE character_inventory_items SET item_core=json_set(item_core,'$.Kind',1) WHERE item_uid=$p0", legacyStoneUid);
        fixture.Sql("UPDATE character_inventory_slots SET list_type=0,slot_index=65 WHERE item_uid=$p0", legacyRuneUid);
        fixture.Sql("UPDATE character_inventory_items SET item_core=json_set(item_core,'$.Kind',2) WHERE item_uid=$p0", legacyRuneUid);
        // 整理用例：把一枚符文预先放到符文页高位，登录后整理应把它回填到页首连续格。
        fixture.Sql("UPDATE character_inventory_slots SET slot_index=160 WHERE item_uid=$p0 AND list_type=46", Uid(campRunes[0]));
        long stoneUid = Uid(stone);
        long materialUid = Uid(costs.MaterialId);
        // 非零扩展高字节必须在真实随机重锻后保留。
        fixture.Sql("UPDATE character_inventory_items SET item_core=json_set(item_core,'$.Extension114.Value',$p0) WHERE item_uid=$p1",
            SocketColors() | 0xFE000000L, stoneUid);
        await fixture.StartAsync();
        long materialBefore = MaterialCount();
        long colorsBefore = SocketColors();
        long colors = colorsBefore;
        using (var peer = Open(fixture))
        {
            peer.Select();
            var body = peer.SpecialContainers[4].Body;
            That(body[0] == 46 && U16(body, 1) == 8 && body.Length == 3 + 8 * 123,
                "登录后护石符文容器含迁移后的八条记录");
            var slots = Enumerable.Range(0, 8).Select(index => BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(3 + index * 123))).ToArray();
            That(slots.Count(slot => slot is >= 0 and < 70) == 2 && slots.Count(slot => slot is >= 70 and < 210) == 6,
                "旧护石与旧符文按真实模板迁入专用分页");
            That(Rows("list_type=0 AND slot_index IN (9,65)") == 0 && Kinds() == "11,11,14,14,14,14,14,14",
                "登录迁移提交后普通格清空且旧分类改写为护石/符文");

            var stoneRow = Occupied(stone);
            var legacyRow = Occupied(legacyStone);
            That(stoneRow.List == 46 && legacyRow.List == 46 &&
                stoneRow.Slot is >= 0 and < 70 && legacyRow.Slot is >= 0 and < 70 && stoneRow.Slot != legacyRow.Slot &&
                stoneRow.Id != legacyRow.Id && stoneRow.Uid != legacyRow.Uid,
                "夹具固定两枚不同护石占用护石页，两边都有物品");
            SwapOccupied(stoneRow, legacyRow, 1, "护石");
            var runeRow = Occupied(campRunes[0]);
            var otherRune = Occupied(campRunes[1]);
            That(runeRow.List == 46 && otherRune.List == 46 &&
                runeRow.Slot is >= 70 and < 210 && otherRune.Slot is >= 70 and < 210 && runeRow.Slot != otherRune.Slot &&
                runeRow.Id != otherRune.Id && runeRow.Count == 3 && otherRune.Count == 3 && runeRow.Uid != otherRune.Uid,
                "夹具固定两枚不同符文整堆占用符文页，互换不并堆");
            SwapOccupied(runeRow, otherRune, runeRow.Count, "符文");

            (short List, short Slot) stoneSlot = SlotOf(stone), materialSlot = SlotOf(costs.MaterialId);
            That(stoneSlot.List == 46 && materialSlot.List == 0, "夹具槽位分布在护石页与普通背包");

            peer.Send(1742, Refine(materialSlot.Slot, stoneSlot.Slot));
            var preload = peer.Expect(0, 14).Body;
            That(preload[0] == 46 && U16(preload, 1) == 1 && U16(preload, 3) == (ushort)stoneSlot.Slot,
                "重锻回执前先投影已提交目标孔色差量");
            peer.Expect(1, 1742, Refined(stone, (uint)stoneSlot.Slot));
            var consumed = peer.Expect(0, 14).Body;
            That(consumed[0] == 0 && U16(consumed, 1) == 1 && U16(consumed, 3) == (ushort)materialSlot.Slot &&
                U32(consumed, 9) == (uint)(perRefine * 2), "重锻回执后同步普通背包扣料的绝对剩余数量");
            var committed = peer.Expect(0, 14).Body;
            That(committed[0] == 46 && U16(committed, 1) == 1 && committed.AsSpan().SequenceEqual(preload),
                "重锻回执后的权威孔色差量与回执前投影同一已提交实例，覆盖客户端预留");
            peer.Barrier();
            colors = SocketColors();
            var face = new[] { (byte)colors, (byte)(colors >> 8), (byte)(colors >> 16) };
            That(MaterialCount() == materialBefore - perRefine && (colors >> 24) == (colorsBefore >> 24) &&
                face.All(color => color is >= 1 and <= 5) && face.Distinct().Count() == 3,
                "重锻按品级扣料并生成三种不重复的孔色，扩展高字节保留");
            // 重锻孔色随机；五种阵营符文都在背包，挑与 210 孔同色的那枚验证装载，211 必然错色。
            var rune = campRunes.First(id => catalog.Rune(id)!.SocketColor == (byte)colors);
            That(catalog.Rune(rune)!.SkillIndex != catalog.Talisman(stone)!.SkillIndex,
                "同孔色异技能符文沿真实装载成功链验证，不恢复旧技能限制");
            (short List, short Slot) runeSlot = SlotOf(rune);
            That(runeSlot.List == 46, "符文槽落在护石页");

            peer.Send(19, Move(46, stoneSlot.Slot, stone, 1, 3, 34, 0));
            peer.Expect(1, 19, Ack(46, stoneSlot.Slot, 1, 3, 34));
            var wornDelta = peer.Expect(0, 14).Body;
            That(wornDelta[0] == 46 && U16(wornDelta, 1) == 1 && U16(wornDelta, 3) == (ushort)stoneSlot.Slot &&
                U32(wornDelta, 5) == uint.MaxValue, "穿戴后护石页源格以清空条目同步，目标槽由ACK19承担");
            var wearActor = peer.Expect(0, 2).Body;
            int appearance = 134 + NameWidth("Talisman");
            That(wearActor[133 + NameWidth("Talisman")] == 1 && wearActor[appearance] == 34 &&
                U32(wearActor, appearance + 1) == (uint)stone, "穿戴护石后Actor携带34号槽真实模板");
            var wearPlayer = peer.Expect(0, 2).Body;
            That(wearPlayer[126] == 1, "穿戴护石后本人Player装备数包含34号槽");
            peer.Barrier();
            That(Rows("list_type=3 AND slot_index=34") == 1, "护石穿戴到装备槽34并提交");

            peer.Send(1659, Equip(210, (ushort)runeSlot.Slot, rune));
            var socketPreload = peer.Expect(0, 14).Body;
            That(socketPreload[0] == 46 && U16(socketPreload, 1) == 1 && U16(socketPreload, 3) == 210 &&
                U32(socketPreload, 5) == (uint)rune, "装载回执前先投影210单枚已提交实例");
            peer.Expect(1, 1659, Equipped(210, rune));
            var loaded = peer.Expect(0, 14).Body;
            That(loaded[0] == 46 && U16(loaded, 1) == 2 && U16(loaded, 3) == (ushort)runeSlot.Slot && U32(loaded, 9) == 2 &&
                U16(loaded, 3 + 123) == 210 && U32(loaded, 3 + 123 + 2) == (uint)rune && U32(loaded, 3 + 123 + 6) == 1,
                "装载后同页差量同步源堆剩余两枚与210单枚实例");
            That(loaded.AsSpan(3 + 123, 123).SequenceEqual(socketPreload.AsSpan(3)),
                "装载预载与回执后孔位差量是同一已提交条目");
            // 符文只改容器 46 的 210–215：不改变外观、不进属性，装载后不得补发本人 USERINFO。补发的
            // USERINFO 会走 0267C4F0→023C3A90 重灌六符文向量并触发护石面板刷新（00698B70），是图鉴
            // 符文页在按技能筛选时退回「所有技能」的唯一未排除触发路径（#747）。这里断言差量之后直接
            // 就是 216 格重发，中间没有 USERINFO。
            var loadedResync = peer.Expect(0, 14).Body;
            That(loadedResync[0] == 46 && U16(loadedResync, 1) == 216 && loadedResync.Length == 3 + 216 * 123 &&
                U16(loadedResync, 3 + 210 * 123) == 210 && U32(loadedResync, 3 + 210 * 123 + 2) == (uint)rune &&
                U32(loadedResync, 3 + runeSlot.Slot * 123 + 6) == 2 &&
                U32(loadedResync, 3 + 215 * 123 + 2) == uint.MaxValue,
                "装载成功后以覆盖全部216格的容器46差量重发：210与源堆取提交结果，空槽为清空条目，中间不插USERINFO");
            peer.Barrier();
            That(Rows("list_type=46 AND slot_index=210") == 1 && StackCount(rune) == 2, "装载符文从堆叠分出一枚落到210");

            // 重放相同装载是原规则允许的替换：旧枚归还、再装一枚，总量不能减少。
            peer.Send(1659, Equip(210, (ushort)runeSlot.Slot, rune));
            var replayPreload = peer.Expect(0, 14).Body;
            That(replayPreload[0] == 46 && U16(replayPreload, 1) == 1 &&
                U16(replayPreload, 3) == 210 && U32(replayPreload, 5) == (uint)rune,
                "重放装载仍先投影提交后的210实例");
            peer.Expect(1, 1659, Equipped(210, rune));
            var replayDelta = peer.Expect(0, 14).Body;
            That(replayDelta.AsSpan().SequenceEqual(replayPreload),
                "相同装载重放只更换孔位实例，源堆数量不变且差量等于预载");
            var replayResync = peer.Expect(0, 14).Body;
            That(replayResync[0] == 46 && U16(replayResync, 1) == 216 &&
                replayResync.AsSpan(3 + 210 * 123, 123).SequenceEqual(replayPreload.AsSpan(3)) &&
                U32(replayResync, 3 + runeSlot.Slot * 123 + 6) == 2,
                "重放后直接216格同步且源堆仍两枚，不插USERINFO");
            peer.Barrier();
            That(Rows("list_type=46 AND slot_index=210") == 1 && StackCount(rune) == 2,
                "重放装载不重复扣除符文");

            peer.Send(19, Move(3, (short)ItemSlotBoundService.TalismanEquipmentStart, stone, 1, 46, stoneSlot.Slot, 0));
            peer.Expect(1, 19, Rejected(3, 46));
            peer.Barrier();
            That(Rows("list_type=3 AND slot_index=34") == 1, "带已装符文的护石卸下被拒绝，穿戴槽保持不变");

            peer.Send(1659, Equip(211, (ushort)runeSlot.Slot, rune));
            peer.Expect(1, 1659, Failure(23));
            var restore = peer.Expect(0, 14).Body;
            That(restore[0] == 46 && U16(restore, 1) == 216 && restore.Length == 3 + 216 * 123 &&
                U16(restore, 3 + 210 * 123) == 210 && U32(restore, 3 + 210 * 123 + 2) == (uint)rune &&
                U32(restore, 3 + 211 * 123 + 2) == uint.MaxValue,
                "拒绝装载后同样以216格差量恢复：210保持已装实例、211仍为空槽，客户端状态与存档一致");
            peer.Barrier();
            That(Rows("list_type=46 AND slot_index=211") == 0 && StackCount(rune) == 2, "孔色不一致被拒绝且不扣材料");

            peer.Send(1660, Remove(210));
            var clearedPreload = peer.Expect(0, 14).Body;
            That(clearedPreload[0] == 46 && U16(clearedPreload, 1) == 1 && U16(clearedPreload, 3) == 210 &&
                U32(clearedPreload, 5) == uint.MaxValue, "卸除回执前先投影210清空条目");
            peer.Expect(1, 1660, Removed(210));
            var returned = peer.Expect(0, 14).Body;
            That(returned[0] == 46 && U16(returned, 1) == 2 && U16(returned, 3) == (ushort)runeSlot.Slot &&
                U32(returned, 9) == 3 && U16(returned, 3 + 123) == 210 && U32(returned, 3 + 123 + 2) == uint.MaxValue,
                "卸除后同页差量合回三枚并清空210");
            That(returned.AsSpan(3 + 123, 123).SequenceEqual(clearedPreload.AsSpan(3)),
                "卸除预载与回执后孔位差量是同一清空条目");
            // 卸除同样只发容器差量：210 清空后直接是 216 格重发，不得插入本人 USERINFO。
            var removedResync = peer.Expect(0, 14).Body;
            That(removedResync[0] == 46 && U16(removedResync, 1) == 216 && removedResync.Length == 3 + 216 * 123 &&
                U32(removedResync, 3 + 210 * 123 + 2) == uint.MaxValue &&
                U32(removedResync, 3 + runeSlot.Slot * 123 + 6) == 3,
                "卸除成功后同样以216格差量重发：210清空、源堆合回三枚，中间不插USERINFO");
            peer.Barrier();
            That(Rows("list_type=46 AND slot_index=210") == 0 && StackCount(rune) == 3, "卸除符文合回背包并清空装载位");

            peer.Send(1660, Remove(210));
            peer.Expect(1, 1660, Failure(23));
            var emptyReplay = peer.Expect(0, 14).Body;
            That(emptyReplay.AsSpan().SequenceEqual(removedResync),
                "空孔重复卸除只拒绝并恢复同一216格状态，不重复归还符文");
            peer.Barrier();
            That(StackCount(rune) == 3 && Rows("list_type=46 AND slot_index=210") == 0,
                "空孔卸除重放保持三枚源堆与空孔");

            peer.Send(1659, new byte[7]);
            peer.Expect(1, 1659, Failure(23));
            peer.Barrier();

            var parkedRune = fixture.Query("""
                SELECT json_extract(i.item_core,'$.ItemId'), json_extract(i.item_core,'$.Value')
                FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid)
                WHERE s.character_id=$p0 AND s.list_type=46 AND s.slot_index=160
                """, peer.Id).Single();
            int parkedId = Convert.ToInt32(parkedRune[0]);
            int parkedCount = Convert.ToInt32(parkedRune[1]);
            peer.Send(19, Move(46, 160, parkedId, parkedCount, 46, 180, 0));
            peer.Expect(1, 19, Ack(46, 160, parkedCount, 46, 180));
            peer.Barrier(packet => That(packet.Opcode is not (13 or 14 or 1681),
                "符文同容器整堆移位只回ACK，不重置符文页也不标记新物品"));
            var spareStone = fixture.Query("""
                SELECT s.slot_index, json_extract(i.item_core,'$.ItemId')
                FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid)
                WHERE s.character_id=$p0 AND s.list_type=46 AND s.slot_index BETWEEN 0 AND 69
                """, peer.Id).Single();
            peer.Send(19, Move(46, Convert.ToInt16(spareStone[0]), Convert.ToInt32(spareStone[1]), 1, 46, 20, 0));
            peer.Expect(1, 19, Ack(46, Convert.ToInt16(spareStone[0]), 1, 46, 20));
            peer.Barrier(packet => That(packet.Opcode is not (13 or 14 or 1681),
                "护石同容器移位只回ACK，不发NOTI13以免重置图鉴筛选，也不标记新物品"));
            // 护石窗口整理：分类18=符文页。只发一次容器46全量，不再先用1681把已有符文标成新物品。
            peer.Send(20, Convert.FromHexString("06000000102E18122000"));
            peer.Expect(1, 20, [1, 4, 0, 0, 0, 8, 46, 16, 0]);
            var sortList = peer.Expect(0, 13).Body;
            That(sortList[0] == 46 && U16(sortList, 1) == 7 && sortList.Length == 3 + 7 * 123,
                "整理成功后只发一次容器46全量，客户端按新槽位重绘且不标记新物品");
            peer.Barrier(packet => That(packet.Opcode is not (14 or 1681), "护石整理之后不再补差量"));
            peer.Barrier();
            That(Rows("list_type=46 AND slot_index=160") == 0 && Rows("list_type=46 AND slot_index BETWEEN 70 AND 79") == 6,
                "符文页整理把六枚符文连续回填到页首，高位空槽被收回");

            (short List, short Slot, long Uid, int Id, int Count) Occupied(int itemId)
            {
                var row = fixture.Query("""
                    SELECT s.list_type, s.slot_index, s.item_uid, json_extract(i.item_core,'$.ItemId'), json_extract(i.item_core,'$.Value')
                    FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid)
                    WHERE s.character_id=$p0 AND json_extract(i.item_core,'$.ItemId')=$p1
                    """, character, itemId).Single();
                return (Convert.ToInt16(row[0]), Convert.ToInt16(row[1]), Convert.ToInt64(row[2]),
                    Convert.ToInt32(row[3]), Convert.ToInt32(row[4]));
            }
            // 双占用互换的 ACK 数量固定为 1：同容器分支不读 movedCount。请求仍须带整堆数量，否则服务端拒绝并不会交换。
            void SwapOccupied((short List, short Slot, long Uid, int Id, int Count) left,
                (short List, short Slot, long Uid, int Id, int Count) right, int requestCount, string name)
            {
                peer.Send(19, Move(46, left.Slot, left.Id, requestCount, 46, right.Slot, right.Id));
                peer.Expect(1, 19, Ack(46, left.Slot, 1, 46, right.Slot));
                peer.Barrier(packet => That(packet.Opcode is not (13 or 14 or 1681),
                    $"{name}双占用互换只回ACK，不发13/14/1681"));
                var movedLeft = Occupied(left.Id);
                var movedRight = Occupied(right.Id);
                That(movedLeft.Slot == right.Slot && movedLeft.Uid == left.Uid && movedLeft.Count == left.Count &&
                    movedRight.Slot == left.Slot && movedRight.Uid == right.Uid && movedRight.Count == right.Count,
                    $"{name}互换后槽位对调，UID与数量不变");
                peer.Send(19, Move(46, right.Slot, left.Id, requestCount, 46, left.Slot, right.Id));
                peer.Expect(1, 19, Ack(46, right.Slot, 1, 46, left.Slot));
                peer.Barrier(packet => That(packet.Opcode is not (13 or 14 or 1681),
                    $"{name}换回原槽只回ACK，不发13/14/1681"));
                var backLeft = Occupied(left.Id);
                var backRight = Occupied(right.Id);
                That(backLeft.Slot == left.Slot && backLeft.Uid == left.Uid && backLeft.Count == left.Count &&
                    backRight.Slot == right.Slot && backRight.Uid == right.Uid && backRight.Count == right.Count,
                    $"{name}换回后UID与数量仍在原槽");
            }
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            var body = peer.SpecialContainers[4].Body;
            That(body[0] == 46 && U16(body, 1) == 7 && body.Length == 3 + 7 * 123,
                "重连后护石页恢复背包内七件，穿戴护石不再重复下发");
            That(Rows("list_type=3 AND slot_index=34") == 1 && Rows("list_type=46 AND slot_index=210") == 0 &&
                SocketColors() == colors, "重连保持穿戴、卸除结果与重锻孔色");
            peer.Barrier();
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "护石 TCP 夹具无外键或完整性异常");

        // 合成预载：三个互不相同的同品级符文格，前两格各一枚（引用一次即扣空），第三格三枚（引用一次留两枚）。
        // 客户端 0x067A 回执只清空合成窗格并重画、不写物品容器，所以回执前的差量必须把三个材料格与产物槽
        // 一起送到，否则材料会先画回背包再消失；扣空的格必须是清空条目，没扣完的格必须带提交后的剩余数量。
        var compoundCosts = catalog.TalismanCompoundCosts();
        var compoundMaterials = PvfScript.List(resources.Archive.ReadText("stackable/stackable.lst"))
            .Where(row => row.Value.StartsWith("rune/", StringComparison.OrdinalIgnoreCase))
            .Select(row => catalog.Rune(row.Key))
            .Where(definition => definition is not null && definition.AllowsUse(95, 0, 1) &&
                compoundCosts.TryGet(0, 0, definition.Grade, out _, out _, out _, out _))
            .GroupBy(definition => definition!.Grade)
            .Select(group => group.Select(definition => definition!.Item.Id).Distinct().Take(3).ToArray())
            .FirstOrDefault(ids => ids.Length == 3);
        That(compoundMaterials is not null, "匹配资源提供三种同品级可按普通规则合成的符文");
        var materials = compoundMaterials!;
        That(compoundCosts.TryGet(0, 0, catalog.Rune(materials[0])!.Grade,
                out int compoundCount, out int compoundGold, out _, out _) && compoundCount == 3,
            "匹配资源普通合成按三件结算材料与金币");
        await using (var compoundFixture = new TcpTestFixture(source, Path.Combine(root, "talisman-compound"), resources: resources))
        {
            var compoundRepo = new CharacterRepository(compoundFixture.Database);
            var compoundOwners = new AccountOwners();
            using var owner = compoundOwners.Acquire(compoundFixture.Options.DevelopmentAccount, () => { });
            var compoundService = new InventoryService(resources.ItemResources.Items, resources.ItemResources.GoldLimits,
                resources.ItemResources.EpicPieces);
            var role = compoundRepo.Create(owner, new("Compound", resources.Towns.Room, resources.Towns.InitialReturn)
                { Job = 0, Level = 95, GrowType = 1, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills });
            role = InventoryService.Commit(compoundRepo, owner, role,
                compoundService.Grant(role,
                    [.. materials.Select((id, index) => new InventoryReward(id, index == 2 ? compoundCount : 1))]));
            role = InventoryService.Commit(compoundRepo, owner, role,
                new CurrencyService(resources.ItemResources.GoldLimits).Grant(role, CurrencyKind.Gold, compoundGold));
            var compoundCharacter = role.Id;
            await compoundFixture.StartAsync();
            using var peer = Open(compoundFixture);
            peer.Select();
            var page = peer.SpecialContainers[4].Body;
            That(page[0] == 46 && U16(page, 1) == 3, "合成夹具的三枚符文登录后全部落在护石页");
            var materialSlots = new short[3];
            for (int index = 0; index < 3; index++)
            {
                short slot = BinaryPrimitives.ReadInt16LittleEndian(page.AsSpan(3 + index * 123));
                That(slot is >= 70 and < 210 && materials.Contains((int)U32(page, 3 + index * 123 + 2)),
                    "三枚符文材料落在符文页");
                materialSlots[index] = slot;
            }
            peer.Send(1658, Compound(0, 0, 0, materialSlots));
            var compoundPreload = peer.Expect(0, 14).Body;
            var compoundAck = peer.Expect(1, 1658);
            That(compoundAck.Body is [1, _, _, _, _, _, _, _], "合成成功回执带成功标志");
            var productSlot = U16(compoundAck.Body, 1);
            uint product = U32(compoundAck.Body, 3);
            var preloaded = Enumerable.Range(0, U16(compoundPreload, 1))
                .Select(index => (Slot: (int)U16(compoundPreload, 3 + index * 123),
                    Id: U32(compoundPreload, 3 + index * 123 + 2), Count: U32(compoundPreload, 3 + index * 123 + 6)))
                .ToDictionary(entry => entry.Slot);
            That(compoundPreload[0] == 46 && materialSlots.All(slot => preloaded.ContainsKey(slot)),
                "合成回执前的预载覆盖全部材料格，客户端重画时不再把材料画回背包");
            // 扣空的两格必须是清空条目；第三格留两枚。产物并入材料格时按回执槽位与合并后数量断言。
            That(materialSlots.Take(2).All(slot => slot == productSlot
                    ? preloaded[slot].Id == product && preloaded[slot].Count == 1
                    : preloaded[slot].Id == uint.MaxValue) &&
                (materialSlots[2] == productSlot
                    ? preloaded[materialSlots[2]].Id == product && preloaded[materialSlots[2]].Count == compoundCount
                    : preloaded[materialSlots[2]].Id == (uint)materials[2] &&
                        preloaded[materialSlots[2]].Count == compoundCount - 1) &&
                preloaded.Values.All(entry => materialSlots.Any(slot => entry.Slot == slot) ||
                    entry.Slot == productSlot && entry.Id == product),
                "预载材料格按提交后状态下发：扣空为清空条目、未扣完保留剩余数量，产物槽携带回执同一实例");
            peer.SendBarrier();
            var tail = new List<WirePacket>();
            WirePacket packet;
            while ((packet = peer.Read()) is not { Kind: 1, Opcode: 1347 }) tail.Add(packet);
            That(tail.Count > 0 && tail.All(item => item is { Kind: 0, Opcode: 14 }) &&
                tail.Any(item => item.Body[0] == 46),
                "合成回执后只补发金币与护石页差量，材料格由预载先行清空");
            peer.Barrier();
            // 前两格扣空后不再有行；第三格留两枚，产物并入时合并为三枚，否则另占一行。
            That(compoundFixture.Query("PRAGMA integrity_check") is [["ok"]] &&
                Convert.ToInt64(compoundFixture.Query(
                    "SELECT COUNT(*) FROM character_inventory_slots WHERE character_id=$p0 AND list_type=46 AND slot_index<210",
                    compoundCharacter)[0][0]) == (materialSlots[2] == productSlot ? 1 : 2) &&
                Convert.ToInt64(compoundFixture.Query(
                    "SELECT json_extract(i.item_core,'$.Value') FROM character_inventory_slots s " +
                    "JOIN character_inventory_items i ON i.item_uid=s.item_uid " +
                    "WHERE s.character_id=$p0 AND s.list_type=46 AND s.slot_index=$p1",
                    compoundCharacter, materialSlots[2])[0][0]) ==
                    (materialSlots[2] == productSlot ? compoundCount : compoundCount - 1),
                "合成夹具只留下产物与第三格的两枚余量，前两枚材料格释放");
        }

        long Uid(int itemId) => Convert.ToInt64(fixture.Query(
            "SELECT s.item_uid FROM character_inventory_slots s JOIN character_inventory_items i ON i.item_uid=s.item_uid " +
            "WHERE s.character_id=$p0 AND json_extract(i.item_core,'$.ItemId')=$p1", character, itemId)[0][0]);
        (short List, short Slot) SlotOf(int itemId)
        {
            var row = fixture.Query(
                "SELECT s.list_type,s.slot_index FROM character_inventory_slots s JOIN character_inventory_items i ON i.item_uid=s.item_uid " +
                "WHERE s.character_id=$p0 AND json_extract(i.item_core,'$.ItemId')=$p1 ORDER BY s.list_type,s.slot_index LIMIT 1",
                character, itemId)[0];
            return (Convert.ToInt16(row[0]), Convert.ToInt16(row[1]));
        }
        long Rows(string where) => Convert.ToInt64(fixture.Query(
            $"SELECT COUNT(*) FROM character_inventory_slots WHERE character_id=$p0 AND {where}", character)[0][0]);
        long StackCount(int itemId) => Convert.ToInt64(fixture.Query(
            "SELECT json_extract(i.item_core,'$.Value') FROM character_inventory_slots s JOIN character_inventory_items i ON i.item_uid=s.item_uid " +
            "WHERE s.character_id=$p0 AND s.list_type=46 AND json_extract(i.item_core,'$.ItemId')=$p1 AND s.slot_index<210",
            character, itemId)[0][0]);
        string Kinds() => string.Join(",", fixture.Query(
            "SELECT json_extract(i.item_core,'$.Kind') FROM character_inventory_slots s JOIN character_inventory_items i ON i.item_uid=s.item_uid " +
            "WHERE s.character_id=$p0 AND s.list_type=46 ORDER BY json_extract(i.item_core,'$.Kind')", character)
            .Select(row => Convert.ToInt64(row[0])).Order());
        long MaterialCount() => Convert.ToInt64(fixture.Query(
            "SELECT json_extract(item_core,'$.Value') FROM character_inventory_items WHERE item_uid=$p0", materialUid)[0][0]);
        long SocketColors() => Convert.ToInt64(fixture.Query(
            "SELECT json_extract(item_core,'$.Extension114.Value') FROM character_inventory_items WHERE item_uid=$p0", stoneUid)[0][0]);
    }

    static (int Stone, int LegacyStone, int[] CampRunes, int LegacyRune) Pick(ItemCatalog catalog, PvfArchive pvf, TalismanRefineCost costs)
    {
        const byte level = 95, job = 0, grow = 1;
        var stones = PvfScript.List(pvf.ReadText("equipment/equipment.lst"))
            .Where(row => row.Value.StartsWith("character/common/talisman/", StringComparison.OrdinalIgnoreCase))
            .Select(row => catalog.Talisman(row.Key))
            .Where(definition => definition is { IsPlaceholder: false } && definition.AllowsUse(level, job, grow) &&
                costs.CountsByRarity.ContainsKey(catalog.Get(definition.Item.Id).Rarity))
            .Select(definition => definition!.Item.Id).Distinct().ToArray();
        That(stones.Length >= 2, "匹配资源提供至少两枚可重锻的可用护石");
        var runes = PvfScript.List(pvf.ReadText("stackable/stackable.lst"))
            .Where(row => row.Value.StartsWith("rune/", StringComparison.OrdinalIgnoreCase))
            .Select(row => catalog.Rune(row.Key))
            .Where(definition => definition is not null && definition.AllowsUse(level, job, grow))
            .Select(definition => definition!.Item.Id).Distinct().ToArray();
        // 重锻后孔色随机，只有背包里备齐五色符文才能稳定挑出与目标孔同色的一枚。
        var campRunes = runes.Where(id => catalog.Rune(id)!.SkillIndex != catalog.Talisman(stones[0])!.SkillIndex)
            .GroupBy(id => catalog.Rune(id)!.Camp).Select(group => group.First()).ToArray();
        That(campRunes.Length == 5, "匹配资源提供五种阵营各一枚与护石不同技能的可用符文");
        return (stones[0], stones[1], campRunes, runes.First(id => !campRunes.Contains(id)));
    }

    static byte[] Equip(ushort target, ushort source, int itemId) => Build(writer =>
    {
        writer.Write(target);
        writer.Write(source);
        writer.Write(itemId);
    });
    static byte[] Remove(ushort target) => Build(writer => writer.Write(target));
    // 13B 表单前缀、族、模式、u32 件数、u32 规则、五个 i16 源引用（空位为 0xFFFF）。
    static byte[] Compound(byte family, byte mode, int rule, short[] slots) => Build(writer =>
    {
        writer.Write(new byte[13]);
        writer.Write(family);
        writer.Write(mode);
        writer.Write((uint)slots.Length);
        writer.Write(rule);
        for (int index = 0; index < 5; index++) writer.Write(index < slots.Length ? slots[index] : (short)-1);
    });
    static byte[] Refine(short materialSlot, short stoneSlot) => Build(writer =>
    {
        writer.Write(new byte[13]);
        writer.Write((int)materialSlot);
        writer.Write((int)stoneSlot);
    });
    static byte[] Equipped(ushort target, int itemId) => Build(writer =>
    {
        writer.Write((byte)1);
        writer.Write(target);
        writer.Write(itemId);
    });
    static byte[] Removed(ushort target) => Build(writer =>
    {
        writer.Write((byte)1);
        writer.Write(target);
    });
    static byte[] Refined(int itemId, uint target) => Build(writer =>
    {
        writer.Write((byte)1);
        writer.Write(itemId);
        writer.Write(target);
        writer.Write(0u);
    });
    static byte[] Failure(ushort error) => Build(writer =>
    {
        writer.Write((byte)0);
        writer.Write(error);
    });
    // ACK19 拒绝：成功标志0、错误码、源容器、目标容器。
    static byte[] Rejected(byte sourceList, byte destinationList) => Build(writer =>
    {
        writer.Write((byte)0);
        writer.Write((ushort)23);
        writer.Write(sourceList);
        writer.Write(destinationList);
    });
    static byte[] Move(byte fromList, short fromSlot, int fromId, int count, byte toList, short toSlot, int toId) => Build(writer =>
    {
        writer.Write(fromList); writer.Write(fromSlot); writer.Write(fromId); writer.Write(count);
        writer.Write(toList); writer.Write(toSlot); writer.Write(toId); writer.Write(0); writer.Write(-1); writer.Write((ushort)0);
    });
    static byte[] Ack(byte fromList, short fromSlot, int count, byte toList, short toSlot) => Build(writer =>
    {
        writer.Write((byte)1); writer.Write(fromList); writer.Write(fromSlot); writer.Write(count); writer.Write(toList); writer.Write(toSlot);
    });
}
