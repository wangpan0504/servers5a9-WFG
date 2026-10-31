using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunCreatureAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "creature-dungeon"), resources: resources,
            clock: new ConsumableUseSessionTests.FakeClock());
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("PetDungeon", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get);
            repository.Create(owner, candidate with { Inventory = candidate.Inventory with { Items = candidate.Inventory.Items
                .Add(new(0, CreatureRules.EquippedSlot, new(63032, ItemKind.Creature, 0) { Attr = 7, Extension27 = 91 })
                    { Creature = new() { Name = "同伴", Experience = 314, Level = 19, Satiety = 75 } }) } });
            var victoria = resources.ItemResources.Creatures.Get(400990444)!;
            repository.Create(owner, candidate with { Name = "VictoriaRun", Inventory = candidate.Inventory with { Items = candidate.Inventory.Items
                .Add(new(0, CreatureRules.EquippedSlot, new(400990444, ItemKind.Creature, 0))
                    { Creature = new() { Name = "雷光之箭维多利亚", Level = 30, Satiety = 75,
                        Mode = victoria.UsesExperienceItems ? (byte)1 : (byte)0,
                        Mode1Field0 = victoria.UsesExperienceItems ? (byte)10 : (byte)0,
                        Mode1Field1 = victoria.UsesExperienceItems ? (byte)5 : (byte)0 } }) } });
        }
        await fixture.StartAsync();
        using var peer = Open(fixture);
        peer.Select(); EntryTown(peer);
        byte[] map = Enter(peer);
        (byte X, byte Y)[] route = [(1, 1), (2, 1), (2, 0), (3, 0), (4, 0)];
        int experienceNotices = 0, evolutions = 0, details = 0;
        for (int room = 0; room < 6; room++)
        {
            Load(peer); Drain();
            for (int index = 0; index < map[18]; index++)
            {
                int offset = 19 + index * 22;
                if (map[offset + 21] != 0) continue;
                peer.Send(39, Death(U16(map, offset + 6), peer.Id, map[offset + 12])); Drain();
            }
            That(Pet().Creature is { Experience: 314, Level: 19 } && experienceNotices == 0,
                "击杀和加载不直接发宠物经验，等待本次实际通关事务");
            if (room < 5)
            {
                var move = new byte[100]; move[0] = route[room].X; move[1] = route[room].Y;
                peer.Send(45, move); peer.Expect(1, 45, [1]); map = peer.Expect(0, 29).Body;
            }
        }
        byte[] report = DungeonSettlementProtocolTests.Report(peer.Id, 99);
        peer.Send(46, report); Drain();
        var pet = Pet();
        That(pet.Core.ItemId == 63033 && pet.Core.Attr == 7 && pet.Core.Extension27 == 91 &&
            pet.Creature is { Level: 20, Experience: 320, Satiety: 75, Name: "同伴" } && experienceNotices == 1 && evolutions == 1 && details == 1,
            "真实六房通关按房间消耗点加6经验，原UID进化；102/106/105各发一次而不重建玩家实体");
        That(fixture.Query("SELECT count(*) FROM character_dungeon_fatigue_receipts WHERE character_id=$p0", peer.Id) is [[0L]],
            "开发无限疲劳不扣持久疲劳，但不抹掉宠物实际通关成长点");
        long revision = (long)fixture.Query("SELECT revision FROM characters WHERE id=$p0", peer.Id)[0][0];
        peer.Send(46, report); Drain();
        That(Pet() == pet && experienceNotices == 1 && evolutions == 1 && details == 1 &&
            fixture.Query("SELECT revision FROM characters WHERE id=$p0", peer.Id) is [[long actual]] && actual == revision,
            "同报告TCP重放不二次增加宠物经验、进化或递增revision");

        // 同一服务复用截图中的实际模板；加载、击杀与跨房期间不发送卸装或重建宠物的包。
        peer.Dispose();
        using var sample = Open(fixture);
        sample.Select(1); EntryTown(sample);
        var sampleBefore = SamplePet();
        byte[] sampleMap = Enter(sample);
        for (int room = 0; room < 3; room++)
        {
            Load(sample); DrainSample();
            for (int index = 0; index < sampleMap[18]; index++)
            {
                int offset = 19 + index * 22;
                if (sampleMap[offset + 21] != 0) continue;
                sample.Send(39, Death(U16(sampleMap, offset + 6), sample.Id, sampleMap[offset + 12])); DrainSample();
            }
            That(SamplePet() == sampleBefore, "维多利亚Lv30跨房及击杀保留原UID、等级、饱食和穿戴槽");
            if (room == 2) break;
            var move = new byte[100]; move[0] = route[room].X; move[1] = route[room].Y;
            sample.Send(45, move); sample.Expect(1, 45, [1]); sampleMap = sample.Expect(0, 29).Body;
        }

        InventoryItem SamplePet()
        {
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            return repository.LoadCharacter(owner, 1).Inventory.Find(CreatureRules.EquippedSlot)!;
        }
        void DrainSample() => QuestLifecycleTcpTests.DrainNotifications(sample, packet =>
        {
            That(packet.Opcode is not (100 or 105 or 106 or 107), "维多利亚跨房不死亡、重载详情、进化或重置复活");
            if (packet.Opcode == 2) That(packet.Body[0] == 1, "维多利亚战斗期间不重建Actor");
            if (packet.Opcode is 14 or 1681 && packet.Body[0] == 3)
                for (int at = 0; at < U16(packet.Body, 1); at++)
                    That(U16(packet.Body, 3 + at * 123) != 26, "维多利亚战斗期间不重载槽26重置能量");
        });

        InventoryItem Pet()
        {
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            return repository.LoadCharacter(owner, 0).Inventory.Find(CreatureRules.EquippedSlot)!;
        }
        void Drain() => QuestLifecycleTcpTests.DrainNotifications(peer, packet =>
        {
            if (packet.Opcode == 102)
            {
                Bytes(packet.Body, [20, 0, 64, 1, 0, 0], "宠物经验通知读取当前累计320，不误用角色经验");
                experienceNotices++;
            }
            if (packet.Opcode == 106)
            {
                Bytes(packet.Body, [13, 0, (byte)peer.Id, (byte)(peer.Id >> 8)], "原生进化种类与角色引用均为u16");
                evolutions++;
            }
            if (packet.Opcode == 105) details++;
            if (packet.Opcode == 2) That(packet.Body[0] == 1, "副本成长不重发城镇Actor打断现有宠物实体");
            if (packet.Opcode == 14 && packet.Body[0] == 3)
                That(U16(packet.Body, 1) == 1 && U16(packet.Body, 3) == 26 &&
                    BinaryPrimitives.ReadInt32LittleEndian(packet.Body.AsSpan(5)) == 63033, "进化装备差量保留本版完整Core目标槽");
        });
    }
}
