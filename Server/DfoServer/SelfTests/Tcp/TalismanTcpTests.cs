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
            var loadedActor = peer.Expect(0, 2).Body;
            var loadedPlayer = peer.Expect(0, 2).Body;
            var loadedList = peer.Expect(0, 13).Body;
            That(loadedList[0] == 46 && U16(loadedList, 1) == 8 && loadedList.Length == 3 + 8 * 123,
                "装载成功后补发容器46全量，图鉴符文页重新允许连续拖拽");
            var vector = new byte[25];
            vector[0] = 6;
            BinaryPrimitives.WriteInt32LittleEndian(vector.AsSpan(1), rune);
            That(loadedActor[133 + NameWidth("Talisman")] == 1 && loadedPlayer[126] == 1 &&
                loadedPlayer.AsSpan().IndexOf(vector) >= 0, "装载后本人Player的六个符文槽位向量包含210实例，穿戴装备不变");
            peer.Barrier();
            That(Rows("list_type=46 AND slot_index=210") == 1 && StackCount(rune) == 2, "装载符文从堆叠分出一枚落到210");

            peer.Send(19, Move(3, (short)ItemSlotBoundService.TalismanEquipmentStart, stone, 1, 46, stoneSlot.Slot, 0));
            peer.Expect(1, 19, Rejected(3, 46));
            peer.Barrier();
            That(Rows("list_type=3 AND slot_index=34") == 1, "带已装符文的护石卸下被拒绝，穿戴槽保持不变");

            peer.Send(1659, Equip(211, (ushort)runeSlot.Slot, rune));
            peer.Expect(1, 1659, Failure(23));
            var restore = peer.Expect(0, 13).Body;
            That(restore[0] == 46 && U16(restore, 1) == 8, "拒绝装载后回发护石页全量，客户端状态与存档一致");
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
            var removedActor = peer.Expect(0, 2).Body;
            var removedPlayer = peer.Expect(0, 2).Body;
            var removedList = peer.Expect(0, 13).Body;
            That(removedList[0] == 46 && U16(removedList, 1) == 7 && removedList.Length == 3 + 7 * 123,
                "卸除成功后同样补发容器46全量，避免符文页失去响应");
            That(removedActor[133 + NameWidth("Talisman")] == 1 && removedPlayer[126] == 1 &&
                removedPlayer.AsSpan().IndexOf(vector) < 0, "卸除后本人Player不再携带装载符文向量");
            peer.Barrier();
            That(Rows("list_type=46 AND slot_index=210") == 0 && StackCount(rune) == 3, "卸除符文合回背包并清空装载位");

            peer.Send(1659, new byte[7]);
            peer.Expect(1, 1659, Failure(23));
            peer.Barrier();

            // 护石窗口整理：分类18=符文页。回执后先用1681清空重载槽再载入新顺序，最后补发容器46全量。
            peer.Send(20, Convert.FromHexString("06000000102E18122000"));
            peer.Expect(1, 20, [1, 4, 0, 0, 0, 8, 46, 16, 0]);
            var sortClear = peer.Expect(0, 1681).Body;
            That(sortClear[0] == 46 && U16(sortClear, 1) >= 1 && U32(sortClear, 5) == uint.MaxValue,
                "护石窗口整理先用1681清空将重新载入的符文槽");
            var sortDelta = peer.Expect(0, 1681).Body;
            That(sortDelta[0] == 46 && U16(sortDelta, 1) >= 1 && sortDelta.Length == 3 + U16(sortDelta, 1) * 123,
                "整理后以1681载入符文页新顺序");
            var sortList = peer.Expect(0, 13).Body;
            That(sortList[0] == 46 && U16(sortList, 1) == 7 && sortList.Length == 3 + 7 * 123,
                "整理成功后补发容器46全量，客户端按新槽位重绘护石与符文");
            peer.Barrier();
            That(Rows("list_type=46 AND slot_index=160") == 0 && Rows("list_type=46 AND slot_index BETWEEN 70 AND 79") == 6,
                "符文页整理把六枚符文连续回填到页首，高位空槽被收回");
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
        var campRunes = runes.GroupBy(id => catalog.Rune(id)!.Camp).Select(group => group.First()).ToArray();
        That(campRunes.Length == 5, "匹配资源提供五种阵营各一枚可用符文");
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
