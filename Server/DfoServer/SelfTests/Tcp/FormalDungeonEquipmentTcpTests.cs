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
            ("武器", InventoryListType.Main, 12, profile.Equipment.Single(item => item.Slot == 12).Template.Id, 101010438),
            ("防具", InventoryListType.Main, 17, profile.Equipment.Single(item => item.Slot == 17).Template.Id, profile.Equipment.Single(item => item.Slot == 17).Template.Id),
            ("装扮", InventoryListType.Avatar, 0, 101550507, 101550507),
            ("宠物", InventoryListType.Pet, 26, 63000, 63000),
            ("宠物装备", InventoryListType.Pet, 28, 2680000, 2680000),
            ("勋章", InventoryListType.GuildMedal, 32, 100380044, 100380044)
        };
        Character character;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var repository = new CharacterRepository(fixture.Database);
            var catalog = resources.Learning;
            var learning = SkillLearning.Initialize(0, profile.Skills, catalog).State! with
            { Experience = CharacterGrowthCatalog.Load(resources.Archive, catalog).GetLevelStartExperience(95) };
            learning = SkillPointLedger.Synchronize(0, 0, 95, learning, catalog);
            character = repository.Create(owner, new("EquipRoomTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            { Level = 95, AcceptedStoryLevel = 1, Skills = profile.Skills, InitialLearning = learning,
                DungeonState = CharacterDungeonState.NewOrdinary });
            var items = resources.ItemResources;
            var inventory = new InventoryService(items.Items, items.GoldLimits, items.EpicPieces);
            character = InventoryService.Commit(repository, owner, character,
                inventory.Grant(character, families.SelectMany(family => new[] { new InventoryReward(family.First, 1), new InventoryReward(family.Second, 1) }).ToArray()));
        }
        string assets = Assets();
        using (var peer = Open(fixture))
        {
            peer.Select(character.Slot);
            EntryTown(peer); SelectMap(peer);
            byte[] selection = Selection();
            BinaryPrimitives.WriteUInt32LittleEndian(selection.AsSpan(16), 0);
            peer.Send(16, selection); peer.Expect(0, 28);
            byte[] map = peer.Expect(0, 29).Body;
            Load(peer); peer.Expect(0, 36);
            ClearRoom(map);
            int room = 0;
            foreach (var family in families)
            {
                var first = FindBag(family.First);
                var second = FindBag(family.Second, first.Uid);
                Move(first.Slot, family.First, 0);
                Move(second.Slot, family.Second, family.First);
                That(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=3 AND slot_index=$p1", peer.Id, family.Worn)
                    is [[long worn]] && worn == second.Uid, $"副本{family.Name}覆盖换装使用第二个真实实例UID");
                Move(first.Slot, 0, family.Second);
                Move(first.Slot, family.Second, 0);
                That(Assets() == assets, $"副本{family.Name}穿脱交换保留所有UID、核心和特殊详情");
                byte[] move = new byte[100]; move[0] = room++ % 2 == 0 ? (byte)1 : (byte)0; move[1] = 1;
                peer.Send(45, move); peer.Expect(1, 45, [1]); map = peer.Expect(0, 29).Body;
                That(map[0] == move[0] && map[1] == move[1] && map[2] == 0 &&
                    (room == 1 ? map[13] == 1 && U32(map, 14) == 58549 :
                        map.Length == 16 && map[13] == 0 && map[14] == 0 && map[15] == byte.MaxValue),
                    $"副本{family.Name}换装后实际Handler45继续过房，已清房间沿缓存模式恢复");
                Load(peer);
                peer.Expect(0, 36, [2, 0, 156, 0, 0, 0, 154, 0, 0, 0]);
                if (room == 1) ClearRoom(map);
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
                    if (family.Worn == 26) That(peer.Expect(0, 105).Body[0] == 2, "换宠物保留全部详情UID通知");
                    int deltas = family.Worn is >= 26 and <= 29 ? 2 : 1;
                    ushort opcode = family.Bag == InventoryListType.Main ? (ushort)1681 : (ushort)14;
                    for (int index = 0; index < deltas; index++)
                    {
                        var delta = peer.Expect(0, opcode).Body;
                        if (index == 0 && fromId != 0 && toId != 0 && fromId != toId)
                        {
                            That(U32(delta, 5) == uint.MaxValue, "背包不同模板覆盖先清除旧资源");
                            delta = peer.Expect(0, opcode).Body;
                        }
                        That(delta[0] == (index == 0 && deltas == 2 ? 3 : (byte)family.Bag), "保留所属容器及宠物专属差量");
                        if (family.Bag == InventoryListType.Main)
                            That(delta.Length == 126 && U16(delta, 1) == 1 && U16(delta, 3) == bag &&
                                U32(delta, 5) == (toId == 0 ? uint.MaxValue : (uint)toId),
                                "副本普通穿脱保留图标装载和完整返回装备，关闭获得通知但不重置房间");
                    }
                    byte[] player = peer.Expect(0, 2).Body;
                    That(player[0] == 1 && U16(player, 22) == peer.Id && player[126] == Convert.ToInt32(fixture.Query(
                        "SELECT count(*) FROM character_inventory_slots WHERE character_id=$p0 AND list_type=3", peer.Id)[0][0]),
                        "副本换装只刷新已提交Player，不插入完整Actor");
                    peer.Barrier();
                    That(fixture.Snapshot("SELECT * FROM character_dungeon_state WHERE character_id=$p0", peer.Id) == progress,
                        "换装不改副本attempt、怪物key和清房状态");
                }
            }
            peer.Send(42); peer.Expect(1, 42, [1]); peer.Expect(0, 24); peer.Expect(0, 21); peer.Barrier();

            void ClearRoom(byte[] roomMap)
            {
                for (int index = 0; index < roomMap[18]; index++)
                {
                    int offset = 19 + index * 22;
                    peer.Send(39, Death(U16(roomMap, offset + 6), peer.Id, roomMap[offset + 12]));
                    peer.Expect(0, 38); peer.Expect(0, 37);
                }
                peer.Barrier();
            }
        }
        using (var peer = Open(fixture))
        {
            var entry = peer.Select(character.Slot);
            That(entry[3].Body[126] == families.Length && Assets() == assets, "重登恢复六类穿戴及无损UID、核心和详情");
            peer.Barrier();
        }
        string Assets() => fixture.Snapshot("SELECT item_uid,json_remove(item_core,'$.SealFlag') FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", character.Id) +
            fixture.Snapshot("SELECT * FROM inventory_avatar_details WHERE item_uid IN (SELECT item_uid FROM character_inventory_items WHERE character_id=$p0) ORDER BY item_uid", character.Id) +
            fixture.Snapshot("SELECT * FROM inventory_creature_details WHERE item_uid IN (SELECT item_uid FROM character_inventory_items WHERE character_id=$p0) ORDER BY item_uid", character.Id);
    }
}
