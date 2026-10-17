using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    static void VerifyDungeonEquipmentMoves(TcpTestFixture fixture, TcpTestResources resources)
    {
        var profile = resources.Characters.Get(0);
        var families = new (string Name, InventoryListType Bag, short Worn, int First, int Second)[]
        {
            ("宠物", InventoryListType.Pet, 26, 63000, 63001),
            ("宠物幻化", InventoryListType.Pet, 33, 63000, 63023),
            ("武器", InventoryListType.Main, 12, profile.Equipment.Single(item => item.Slot == 12).Template.Id, 101010438),
            ("防具", InventoryListType.Main, 17, profile.Equipment.Single(item => item.Slot == 17).Template.Id, profile.Equipment.Single(item => item.Slot == 17).Template.Id),
            ("装扮", InventoryListType.Avatar, 0, 101550507, 101550507),
            ("真实光环", InventoryListType.Avatar, 9, 401590036, 401590037),
            ("光环外观", InventoryListType.Avatar, 11, 401590036, 401590037),
            ("宠物装备", InventoryListType.Pet, 28, 2680000, 2680000),
            ("勋章", InventoryListType.GuildMedal, 32, 100380044, 100380044)
        };
        Character character;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var repository = new CharacterRepository(fixture.Database);
            var catalog = resources.Learning;
            var learning = SkillLearning.Initialize(0, profile.Skills, catalog).State! with
            { Experience = SelfTestCatalogs.Growth(resources.Archive, catalog).GetLevelStartExperience(95) };
            learning = SkillPointLedger.Synchronize(0, 0, 95, learning, catalog);
            character = repository.Create(owner, new("EquipRoomTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            { Level = 95, AcceptedStoryLevel = 1, Skills = profile.Skills, InitialLearning = learning, EquipmentSlotFlags = 40,
                DungeonState = CharacterDungeonState.NewOrdinary });
            var items = resources.ItemResources;
            var inventory = new InventoryService(items.Items, items.GoldLimits, items.EpicPieces);
            character = InventoryService.Commit(repository, owner, character,
                inventory.Grant(character, families.SelectMany(family => new[] { new InventoryReward(family.First, 1), new InventoryReward(family.Second, 1) })
                    .Append(new InventoryReward(63000, 1)).ToArray()));
        }
        string assets = Assets();
        using (var peer = Open(fixture))
        {
            peer.Select(character.Slot);
            EntryTown(peer); SelectMap(peer);
            short mapSelectPet = Convert.ToInt16(fixture.Query("""
                SELECT s.slot_index FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid)
                WHERE s.character_id=$p0 AND s.list_type=7 AND json_extract(i.item_core,'$.ItemId')=63000 ORDER BY s.slot_index
                """, peer.Id)[0][0]);
            CreatureChange(mapSelectPet, 63000, 0, 0);
            short skinBag = Convert.ToInt16(fixture.Query("""
                SELECT s.slot_index FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid)
                WHERE s.character_id=$p0 AND s.list_type=7 AND json_extract(i.item_core,'$.ItemId')=63023
                """, peer.Id)[0][0]);
            SkinChange(skinBag, 63023, 0, 0, 63000);
            CreatureChange(mapSelectPet, 0, 63000, 0);
            SkinChange(skinBag, 0, 63023, 0, 0);
            That(Assets() == assets, "选图阶段合法穿脱宠物保持状态0及原UID、核心和详情");
            byte[] selection = Selection();
            BinaryPrimitives.WriteUInt32LittleEndian(selection.AsSpan(16), 0);
            peer.Send(16, selection); peer.Expect(0, 28);
            byte[] map = peer.Expect(0, 29).Body;
            Load(peer); peer.Expect(0, 36);
            int room = 0;
            foreach (var family in families)
            {
                var missingCreatureDeclarations = new List<string>();
                var first = FindBag(family.First);
                var second = FindBag(family.Second, first.Uid);
                Move(first.Slot, family.First, 0);
                if (family.Worn == 26)
                {
                    string unchanged = Assets();
                    peer.Send(19, Build(writer =>
                    {
                        writer.Write((byte)7); writer.Write(first.Slot); writer.Write(63000); writer.Write(1);
                        writer.Write((byte)3); writer.Write((short)26); writer.Write(0); writer.Write(0); writer.Write(-1); writer.Write((ushort)0);
                    }));
                    peer.Expect(1, 19, [0, 23, 0, 7, 3]);
                    peer.Barrier();
                    peer.Send(19, Build(writer =>
                    {
                        writer.Write((byte)3); writer.Write((short)26); writer.Write(63000); writer.Write(1);
                        writer.Write((byte)3); writer.Write((short)26); writer.Write(63000); writer.Write(0); writer.Write(-1); writer.Write((ushort)0);
                    }));
                    peer.Expect(1, 19, [1, 3, 26, 0, 0, 0, 0, 0, 3, 26, 0]);
                    peer.Barrier();
                    That(Assets() == unchanged, "副本重复穿戴拒绝与同槽零变化不发布Actor或差量，保持宠物资产");
                    var sameTemplate = FindBag(family.First, first.Uid);
                    Move(sameTemplate.Slot, family.First, family.First);
                    That(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=3 AND slot_index=26", peer.Id)
                        is [[long sameWorn]] && sameWorn == sameTemplate.Uid, "副本同模板不同UID宠物交换仍发布实例变化声明");
                }
                Move(second.Slot, family.Second, family.First);
                That(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=3 AND slot_index=$p1", peer.Id, family.Worn)
                    is [[long worn]] && worn == second.Uid, $"副本{family.Name}覆盖换装使用第二个真实实例UID");
                Move(first.Slot, 0, family.Second);
                Move(first.Slot, family.Second, 0);
                That(missingCreatureDeclarations.Count == 0,
                    $"副本宠物首次穿戴、替换与7→3空源卸装须在Player前声明真实模板/启用位；缺失：{string.Join(",", missingCreatureDeclarations)}");
                That(Assets() == assets, $"副本{family.Name}穿脱交换保留所有UID、核心和特殊详情");
                // 首两房先换装再清怪；后续回访的是已清缓存房间。
                if (room < 2) ClearRoom(map);
                byte[] move = new byte[100]; move[0] = room++ % 2 == 0 ? (byte)1 : (byte)0; move[1] = 1;
                peer.Send(45, move); peer.Expect(1, 45, [1]); map = peer.Expect(0, 29).Body;
                That(map[0] == move[0] && map[1] == move[1] && map[2] == 0 &&
                    (room == 1 ? map[13] == 1 && U32(map, 14) == 58549 :
                        map.Length == 16 && map[13] == 0 && map[14] == 0 && map[15] == byte.MaxValue),
                    $"副本{family.Name}换装后合成请求通过Handler45，已清房间沿缓存模式恢复");
                Load(peer);
                peer.Expect(0, 36, [2, 0, 156, 0, 0, 0, 154, 0, 0, 0]);
                peer.Barrier();

                (short Slot, long Uid) FindBag(int id, long except = 0)
                {
                    var row = fixture.Query("""
                        SELECT s.slot_index,s.item_uid FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid)
                        WHERE s.character_id=$p0 AND s.list_type=$p1 AND json_extract(i.item_core,'$.ItemId')=$p2 AND s.item_uid<>$p3 ORDER BY s.slot_index
                        """, peer.Id, (byte)family.Bag, id, except).First();
                    return (Convert.ToInt16(row[0]), Convert.ToInt64(row[1]));
                }
                void Move(short bag, int fromId, int toId)
                {
                    string progress = fixture.Snapshot("SELECT * FROM character_dungeon_state WHERE character_id=$p0", peer.Id);
                    peer.Send(19, Build(writer =>
                    {
                        writer.Write((byte)family.Bag); writer.Write(bag); writer.Write(fromId); writer.Write(1);
                        writer.Write((byte)3); writer.Write(family.Worn); writer.Write(toId); writer.Write(0); writer.Write(-1); writer.Write((ushort)0);
                    }));
                    peer.Expect(1, 19, Build(writer =>
                    { writer.Write((byte)1); writer.Write((byte)family.Bag); writer.Write(bag); writer.Write(1); writer.Write((byte)3); writer.Write(family.Worn); }));
                    if (family.Worn is 26 or 33) That(peer.Expect(0, 105).Body[0] == 5, "换真实或幻化宠物保留全部详情UID通知");
                    int deltas = family.Worn is >= 26 and <= 29 ? 2 : 1;
                    ushort opcode = family.Bag == InventoryListType.Main ? (ushort)1681 : (ushort)14;
                    for (int index = 0; index < deltas; index++)
                    {
                        var delta = peer.Expect(0, opcode).Body;
                        byte list = index == 0 && deltas == 2 ? (byte)3 : (byte)family.Bag;
                        short slot = list == 3 ? family.Worn : bag;
                        int beforeId = list == 3 ? toId : fromId;
                        int afterId = list == 3 ? fromId : toId;
                        if (family.Bag == InventoryListType.Main && toId != 0 || beforeId != 0 && afterId != 0 && beforeId != afterId)
                        {
                            That(delta[0] == list && U16(delta, 1) == 1 && U16(delta, 3) == slot && U32(delta, 5) == uint.MaxValue,
                                "普通卸装和同模板换装均先清ACK对象，特殊容器仍只清不同模板覆盖");
                            delta = peer.Expect(0, opcode).Body;
                        }
                        That(delta[0] == list, "保留所属容器及宠物专属差量");
                        if (family.Worn == 26)
                            That(delta.Length == 126 && U16(delta, 1) == 1 && U16(delta, 3) == slot &&
                                U32(delta, 5) == (afterId == 0 ? uint.MaxValue : (uint)afterId), "宠物交换差量使用真实模板并清空卸下槽26");
                        if (family.Bag == InventoryListType.Main)
                            That(delta.Length == 126 && U16(delta, 1) == 1 && U16(delta, 3) == bag &&
                                U32(delta, 5) == (toId == 0 ? uint.MaxValue : (uint)toId),
                                "副本普通穿脱保留图标装载和完整返回装备，关闭获得通知但不重置房间");
                    }
                    byte[] player = peer.Expect(0, 2).Body;
                    if (family.Worn is 9 or 11 or 26 or 33)
                    {
                        if (player[0] != 0)
                            missingCreatureDeclarations.Add($"{fromId}/{toId}");
                        else
                        {
                            byte[] actor = player;
                            int nameWidth = checked((int)U32(actor, 123));
                            // 0267DF0B读取Level后第三字节到成员DTO+1C；原生45要求该状态非零。
                            byte memberState = actor[132 + nameWidth];
                            That(memberState == 1, "副本外观Actor须保留已加载成员状态1，不能关闭客户端45发送门禁");
                            if (family.Worn is 9 or 11)
                            {
                                int count = actor[133 + nameWidth];
                                int start = 134 + nameWidth;
                                var auraEntries = Enumerable.Range(0, count)
                                    .Select(index => (Slot: actor[start + index * 25], Id: U32(actor, start + index * 25 + 1)))
                                    .ToArray();
                                That(auraEntries.Where(entry => entry.Slot == family.Worn)
                                    .Select(entry => entry.Id).SequenceEqual(fromId == 0 ? [] : [(uint)fromId]),
                                    "副本Actor中9/11仅投影本次实际穿戴模板，卸下清对应外观槽");
                                if (family.Worn == 11)
                                {
                                    uint realAura = Convert.ToUInt32(fixture.Query("""
                                        SELECT json_extract(i.item_core,'$.ItemId') FROM character_inventory_slots s
                                        JOIN character_inventory_items i USING(item_uid)
                                        WHERE s.character_id=$p0 AND s.list_type=3 AND s.slot_index=9
                                        """, peer.Id).Single()[0]);
                                    That(auraEntries.Any(entry => entry.Slot == 9 && entry.Id == realAura),
                                        "11变化保留独立真实9声明，同屏外观不覆盖真光环");
                                }
                            }
                            else
                            {
                                int creatureOffset = 156 + nameWidth + actor[133 + nameWidth] * 25;
                                uint real = family.Worn == 33 ? 63001u : (uint)fromId;
                                That(U16(actor, 121) == peer.Id && U32(actor, creatureOffset) == real &&
                                    U32(actor, creatureOffset + 4) == 0 && actor[creatureOffset + 8] == (real == 0 ? 0 : 1) &&
                                    U32(actor, 94) == (family.Worn == 33 ? (uint)fromId : 0u),
                                    "副本Actor分别投影真实26与独立幻化；幻化穿脱不覆盖真实宠物模板和启用位");
                            }
                            player = peer.Expect(0, 2).Body;
                        }
                    }
                    That(player[0] == 1 && U16(player, 22) == peer.Id && player[126] == Convert.ToInt32(fixture.Query(
                        "SELECT count(*) FROM character_inventory_slots WHERE character_id=$p0 AND list_type=3", peer.Id)[0][0]),
                        "副本换装保留已提交Player；真实/外观光环与宠物双槽变化先追加Actor声明");
                    peer.Barrier();
                    That(fixture.Snapshot("SELECT * FROM character_dungeon_state WHERE character_id=$p0", peer.Id) == progress,
                        "换装不改副本attempt、怪物key和清房状态");
                }
            }
            // 复用当前普通实例到Boss，取得真实Settlement阶段，不另建副本fixture。
            foreach (var target in new (byte X, byte Y)[] { (1, 1), (1, 0), (1, 1), (2, 1), (2, 0), (3, 0) })
            {
                if (map[0] == target.X && map[1] == target.Y) continue;
                byte[] move = new byte[100]; move[0] = target.X; move[1] = target.Y;
                peer.Send(45, move); peer.Expect(1, 45, [1]); map = peer.Expect(0, 29).Body;
                Load(peer); peer.Expect(0, 36);
                if (map.Length > 16) ClearRoom(map);
            }
            That(U32(map, 14) == 58553, "真实普通Boss房清图后进入结算阶段");
            var auraBags = fixture.Query("""
                SELECT s.slot_index FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid)
                WHERE s.character_id=$p0 AND s.list_type=1 AND json_extract(i.item_core,'$.ItemId')=401590036
                ORDER BY s.slot_index
                """, peer.Id).Select(row => Convert.ToInt16(row[0])).ToArray();
            That(auraBags.Length >= 2, "结算9/11具备两件独立光环实例");
            SettlementAura(9, 102, 0, 401590037);
            SettlementAura(9, auraBags[0], 401590036, 0);
            SettlementAura(11, 103, 0, 401590037);
            SettlementAura(11, auraBags[1], 401590036, 0);
            SettlementAura(11, auraBags[1], 0, 401590036);
            SettlementAura(9, auraBags[0], 0, 401590036);
            SettlementAura(9, 102, 401590037, 0);
            SettlementAura(11, 103, 401590037, 0);
            SkinChange(138, 0, 63023, 1, 63001);
            CreatureChange(139, 0, 63001, 1);
            SkinChange(138, 63023, 0, 1, 0);
            CreatureChange(139, 63001, 0, 1);
            SkinChange(138, 0, 63023, 1, 63001);
            SkinChange(138, 63023, 0, 1, 63001);
            That(Assets() == assets, "结算穿脱保留原UID、核心和详情");
            peer.Send(42); peer.Expect(1, 42, [1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0, 24); peer.Expect(0, 21); peer.Barrier();

            void SettlementAura(short wornSlot, short bag, int fromId, int toId)
            {
                peer.Send(19, Build(writer =>
                {
                    writer.Write((byte)1); writer.Write(bag); writer.Write(fromId); writer.Write(1);
                    writer.Write((byte)3); writer.Write(wornSlot); writer.Write(toId);
                    writer.Write(0); writer.Write(-1); writer.Write((ushort)0);
                }));
                byte[] ack = peer.Expect(1, 19).Body;
                byte[] expectedAck = Build(writer =>
                { writer.Write((byte)1); writer.Write((byte)1); writer.Write(bag); writer.Write(1);
                    writer.Write((byte)3); writer.Write(wornSlot); });
                That(ack.SequenceEqual(expectedAck), $"结算光环ACK: {wornSlot}, {fromId}->{toId}, actual={Convert.ToHexString(ack)}, expected={Convert.ToHexString(expectedAck)}");
                peer.Expect(0, 14);
                byte[] actor = peer.Expect(0, 2).Body;
                int nameWidth = checked((int)U32(actor, 123));
                int count = actor[133 + nameWidth];
                int start = 134 + nameWidth;
                var entries = Enumerable.Range(0, count)
                    .Select(index => (Slot: actor[start + index * 25], Id: U32(actor, start + index * 25 + 1)))
                    .ToArray();
                That(actor[0] == 0 && actor[132 + nameWidth] == 1 &&
                    entries.Where(entry => entry.Slot == wornSlot).Select(entry => entry.Id)
                        .SequenceEqual(fromId == 0 ? [] : [(uint)fromId]),
                    "结算9/11穿脱先发布保留成员状态1的Actor及实际槽模板");
                if (wornSlot == 11)
                {
                    var realAura = fixture.Query("""
                        SELECT json_extract(i.item_core,'$.ItemId') FROM character_inventory_slots s
                        JOIN character_inventory_items i USING(item_uid)
                        WHERE s.character_id=$p0 AND s.list_type=3 AND s.slot_index=9
                        """, peer.Id);
                    uint expectedReal = realAura.Length == 0 ? 0 : Convert.ToUInt32(realAura.Single()[0]);
                    That(entries.Where(entry => entry.Slot == 9).Select(entry => entry.Id)
                        .SequenceEqual(expectedReal == 0 ? [] : [expectedReal]),
                        "结算11变化不覆盖真实9的Actor声明");
                }
                That(peer.Expect(0, 2).Body[0] == 1, "结算光环Actor后才发布Player");
                peer.Barrier();
            }

            void CreatureChange(short bag, int fromId, int toId, byte sceneState)
            {
                peer.Send(19, CreatureMove(bag, fromId, toId));
                peer.Expect(1, 19, [1, 7, (byte)bag, (byte)(bag >> 8), 1, 0, 0, 0, 3, 26, 0]);
                peer.Expect(0, 105); peer.Expect(0, 14); peer.Expect(0, 14);
                byte[] actor = peer.Expect(0, 2).Body;
                int nameWidth = checked((int)U32(actor, 123));
                int creatureOffset = 156 + nameWidth + actor[133 + nameWidth] * 25;
                That(actor[0] == 0 && actor[132 + nameWidth] == sceneState && U32(actor, creatureOffset) == (uint)fromId,
                    "真实选图/结算回执宠物Actor保留对应成员状态及提交后模板");
                That(peer.Expect(0, 2).Body[0] == 1, "宠物Actor之后才发Player");
                peer.Barrier();
            }

            void SkinChange(short bag, int fromId, int toId, byte sceneState, uint real)
            {
                peer.Send(19, Build(writer =>
                {
                    writer.Write((byte)7); writer.Write(bag); writer.Write(fromId); writer.Write(1);
                    writer.Write((byte)3); writer.Write((short)33); writer.Write(toId); writer.Write(0); writer.Write(-1); writer.Write((ushort)0);
                }));
                peer.Expect(1, 19, Build(writer =>
                { writer.Write((byte)1); writer.Write((byte)7); writer.Write(bag); writer.Write(1); writer.Write((byte)3); writer.Write((short)33); }));
                peer.Expect(0, 105);
                That(peer.Expect(0, 14).Body[0] == 7, "33由ACK移动，宠物背包仍发布完整提交差量");
                byte[] actor = peer.Expect(0, 2).Body;
                int nameWidth = checked((int)U32(actor, 123));
                int creatureOffset = 156 + nameWidth + actor[133 + nameWidth] * 25;
                That(actor[0] == 0 && actor[132 + nameWidth] == sceneState && U32(actor, creatureOffset) == real &&
                    U32(actor, 94) == (real == 0 ? 0u : (uint)fromId), "选图及结算幻化保留成员状态；无真宠时清外观而不清幻化资产");
                That(peer.Expect(0, 2).Body[0] == 1, "33独立外观声明之后才发Player");
                peer.Barrier();
            }

            byte[] CreatureMove(short bag, int fromId, int toId) => Build(writer =>
            {
                writer.Write((byte)7); writer.Write(bag); writer.Write(fromId); writer.Write(1);
                writer.Write((byte)3); writer.Write((short)26); writer.Write(toId); writer.Write(0); writer.Write(-1); writer.Write((ushort)0);
            });

            void ClearRoom(byte[] roomMap)
            {
                var rows = Enumerable.Range(0, roomMap[18]).Select(index => 19 + index * 22)
                    .Where(offset => roomMap[offset + 13] != 9 && roomMap[offset + 14] == 0)
                    .OrderBy(offset => roomMap[offset + 13] == 3);
                foreach (int offset in rows)
                {
                    ushort key = U16(roomMap, offset + 6);
                    bool boss = roomMap[offset + 13] == 3;
                    byte[] report = Death(key, peer.Id, roomMap[offset + 12]);
                    if (boss) { report[55] = 1; SealDeath(report); }
                    peer.Send(39, report);
                    peer.Expect(0, 38); peer.Expect(0, 37);
                    if (boss) { peer.Expect(0, 115, [1, 1, (byte)key, (byte)(key >> 8)]); peer.Expect(0, 31, []); }
                }
                peer.Barrier();
            }
        }
        using (var peer = Open(fixture))
        {
            var entry = peer.Select(character.Slot);
            That(entry[3].Body[126] == families.Length && entry[3].Body[21] == 1 && U32(entry[2].Body, 94) == 63023 && Assets() == assets,
                "重登恢复普通穿戴、真实宠物与幻化，保留开通位及完整UID、核心和详情");
            peer.Barrier();
        }
        string Assets() => fixture.Snapshot("SELECT item_uid,json_remove(item_core,'$.SealFlag') FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", character.Id) +
            fixture.Snapshot("SELECT * FROM inventory_avatar_details WHERE item_uid IN (SELECT item_uid FROM character_inventory_items WHERE character_id=$p0) ORDER BY item_uid", character.Id) +
            fixture.Snapshot("SELECT * FROM inventory_creature_details WHERE item_uid IN (SELECT item_uid FROM character_inventory_items WHERE character_id=$p0) ORDER BY item_uid", character.Id);
    }
}
