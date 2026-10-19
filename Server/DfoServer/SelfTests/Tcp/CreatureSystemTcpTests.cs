using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class CreatureSystemTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        CreatureSessionTests.Run(resources.Towns, resources.Characters);
        DungeonSettlementTests.RunCreatures(resources.Archive, resources.ItemResources);
        CreatureQuestTests.Run(resources);
        var catalog = resources.ItemResources.Creatures;
        var belz = catalog.Get(63032)!;
        That(belz is { Species: 12, EvolutionLevel: 20, MaximumLevel: 50 } &&
            catalog.Get(63033) is { Species: 13, EvolutionQuest: true, EvolutionSpecies: 14 } &&
            catalog.Evolution(belz, 13)?.ItemId == 63033 && catalog.Level(283, belz) == 19 && catalog.Level(315, belz) == 20,
            "原版PVF普通宠物经验表、等级及两段进化资格");
        That(catalog.Get(63407) is { UsesExperienceItems: true, ItemsPerLevel: 10,
            SelectiveGrowth: { ItemA: 10098003, ItemB: 10098002, ExperienceItem: 10098004 } },
            "原版CRE嵌套成长类型和每级喂食次数可被正式解析器读取");
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "creature-system"), resources: resources,
            clock: new ConsumableUseSessionTests.FakeClock());
        var repository = new CharacterRepository(fixture.Database);
        ushort id;
        InventoryItem exchangeMaterial;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("PetTcp", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get);
            var created = repository.Create(owner, candidate with { Inventory = candidate.Inventory with { Items = candidate.Inventory.Items
                .Add(new(0, CreatureRules.EquippedSlot, new(63032, ItemKind.Creature, 0)) { Creature = new() { Satiety = 10 } })
                .Add(new(0, new(InventoryListType.Pet, 189), new(24, ItemKind.CreatureConsumable, 2)))
                .Add(new(0, new(InventoryListType.Pet, 190), new(25, ItemKind.CreatureConsumable, 1))) } });
            id = created.Id;
            var grow = repository.Create(owner, candidate with { Name = "PetGrow", Inventory = candidate.Inventory with { Items = candidate.Inventory.Items
                .Add(new(0, CreatureRules.EquippedSlot, new(63407, ItemKind.Creature, 0))
                    { Creature = new() { Mode = 1, Mode1Field0 = 10, Mode1Field1 = 5, Experience = 199, Level = 20 } })
                .Add(new(0, new(InventoryListType.Main, 65), new(10098003, ItemKind.Consumable, 1)))
                .Add(new(0, new(InventoryListType.Main, 66), new(10098004, ItemKind.Consumable, 1)))
                .Add(new(0, new(InventoryListType.Main, 67), new(10098002, ItemKind.Consumable, 3))) } });
            exchangeMaterial = grow.Inventory.Find(new(InventoryListType.Main,67))!;
            repository.Create(owner, candidate with { Name = "PetExp", Inventory = candidate.Inventory with { Items = candidate.Inventory.Items
                .Add(new(0, CreatureRules.EquippedSlot, new(63407, ItemKind.Creature, 0))
                    { Creature = new() { Mode = 1, Mode1Field0 = 10, Mode1Field1 = 5, Experience = 195, Level = 20 } })
                .Add(new(0, new(InventoryListType.Main, 66), new(10098004, ItemKind.Consumable, 2))) } });
        }
        await fixture.StartAsync();
        long uid = ReadPet().Uid;
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Send(101, Build(writer => writer.Write(peer.Id)));
            peer.Expect(0, 104, Build(writer => writer.Write(peer.Id))); peer.Barrier();
            byte[] message = [3, 0, 0, .. Build(writer => writer.Write((int)peer.Id)), .. Name("你好")];
            peer.Send(122, message);
            peer.Expect(0, 119, [3, .. Build(writer => writer.Write(peer.Id)), 0, .. Name("你好")]); peer.Barrier();
            peer.Send(100, [190, 0, 7, .. Name("小伙伴")]);
            peer.Expect(1, 100, [1, 190, 0, 7]);
            peer.Expect(0, 101, [.. Build(writer => writer.Write(peer.Id)), .. Name("小伙伴")]); peer.Barrier();
            That(ReadPet().Uid == uid && ReadPet().Creature!.Name == "小伙伴" && Count(190) == 0,
                "真实100消费源换名卡，同UID名称及详情原子保存");
            peer.Send(479); peer.Expect(0, 21); peer.Barrier();
            byte[] use = Build(writer => { writer.Write((ushort)189); writer.Write((byte)7); writer.Write(0u); writer.Write(24); writer.Write(0); });
            peer.Send(44, use);
            peer.Expect(1, 44, [1, 189, 0, 7, 0, 0, 0, 0, 24, 0, 0, 0]);
            peer.Expect(0, 103, Build(writer => { writer.Write((int)uid); writer.Write(40); })); peer.Barrier();
            That(Count(189) == 1 && ReadPet().Creature is { Satiety: 40, FeedSkillRecoveryRate: -20 },
                "原生自动/主动喂食共用44，只扣一次饲料，103按i32饱食同步且无获得动画");
            peer.Send(44, use); peer.Closed();
            That(Count(189) == 1, "同请求冷却重放关闭原连接，不重发扣源ACK");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            That(ReadPet().Uid == uid && ReadPet().Creature is { Satiety: 40, Name: "小伙伴" },
                "重新连接保留宠物名称和饱食，不自动再消费饲料");
            string before = State();
            fixture.Sql("CREATE TRIGGER pet_feed_abort AFTER INSERT ON inventory_audit_log WHEN NEW.action_name='creature_feed' " +
                "BEGIN SELECT RAISE(ABORT,'pet rollback'); END");
            peer.Send(44, Build(writer => { writer.Write((ushort)189); writer.Write((byte)7); writer.Write(1u); writer.Write(24); writer.Write(0); }));
            peer.Closed();
            That(State() == before && Count(189) == 1, "饲料与宠物详情已写后的SQL错误整笔回滚，无先行成功ACK");
            fixture.Sql("DROP TRIGGER pet_feed_abort");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(1);
            peer.Send(44, Build(writer => { writer.Write((ushort)65); writer.Write((byte)0); writer.Write(0u); writer.Write(10098003); writer.Write(0); }));
            peer.Expect(1, 44, Build(writer => { writer.Write((byte)1); writer.Write((ushort)65); writer.Write((byte)0); writer.Write(0u); writer.Write(10098003); }));
            peer.Expect(0, 102, [21, 1, 200, 0, 0, 0, 10, 6]);
            peer.Expect(0, 106, [131, 1, (byte)peer.Id, (byte)(peer.Id >> 8)]);
            peer.Expect(0, 105); peer.Expect(0, 14);
            peer.Barrier();
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            {
                var saved = repository.LoadCharacter(owner, 1);
                var evolved = saved.Inventory.Find(CreatureRules.EquippedSlot)!;
                That(catalog.Get(evolved.Core.ItemId) is { Species: 387, UsesExperienceItems: true } &&
                    evolved.Creature is { Level: 21, Experience: 200, Mode1Field1: 6 } && saved.Inventory.Find(new(InventoryListType.Main, 65)) is null &&
                    saved.Inventory.Find(new(InventoryListType.Main, 67)) == exchangeMaterial,
                    "原版PVF A类成长到21级，按亲密度进化387，只消费实际Main源，兑换材料UID与三份数量不变");
            }
        }
        using (var peer = Open(fixture))
        {
            peer.Select(2);
            peer.Send(44, Build(writer => { writer.Write((ushort)66); writer.Write((byte)0); writer.Write(1u); writer.Write(10098004); writer.Write(0); }));
            peer.Expect(1, 44, Build(writer => { writer.Write((byte)1); writer.Write((ushort)66); writer.Write((byte)0); writer.Write(1u); writer.Write(10098004); }));
            peer.Expect(0, 102, [21, 1, 200, 0, 0, 0, 10, 5]);
            peer.Expect(0, 106, [128, 1, (byte)peer.Id, (byte)(peer.Id >> 8)]);
            peer.Expect(0, 105); peer.Expect(0, 14); peer.Barrier();
            using (var peerAgain = Open(fixture))
            {
                peer.Closed(); peerAgain.Select(2);
                peerAgain.Send(44, Build(writer => { writer.Write((ushort)66); writer.Write((byte)0); writer.Write(2u); writer.Write(10098004); writer.Write(0); }));
                peerAgain.Expect(1, 44, Build(writer => { writer.Write((byte)1); writer.Write((ushort)66); writer.Write((byte)0); writer.Write(2u); writer.Write(10098004); }));
                peerAgain.Expect(0, 102, [21, 1, 205, 0, 0, 0, 10, 5]); peerAgain.Barrier();
            }
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "宠物使用、换名与恢复后数据库完整");

        InventoryItem ReadPet()
        {
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            return repository.LoadCharacter(owner, 0).Inventory.Find(CreatureRules.EquippedSlot)!;
        }
        int Count(short slot) => fixture.Query("SELECT item_core FROM character_inventory_items i JOIN character_inventory_slots s USING(item_uid) " +
            "WHERE s.character_id=$p0 AND s.list_type=7 AND s.slot_index=$p1", id, slot) is [var row]
            ? JsonSerializer.Deserialize<ItemCore>((string)row[0])!.Count : 0;
        string State() => fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", id) +
            fixture.Snapshot("SELECT * FROM inventory_creature_details ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots WHERE character_id=$p0 ORDER BY item_uid", id);
    }
}
