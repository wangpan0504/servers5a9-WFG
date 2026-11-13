using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class SpecialContainerTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var itemResources = resources.ItemResources;
        string defaultPetName = CreatureRules.DefaultName(itemResources.Creatures.Get(63000)!.Name);
        var characters = resources.Characters;
        var towns = resources.Towns;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "special-containers"), resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var role = repo.Create(owner, new("SpecialTcp", towns.Room, towns.InitialReturn) { AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills });
            var service = new InventoryService(itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
            InventoryService.Commit(repo, owner, role, service.Grant(role, [new(101550507, 2), new(63000, 2), new(2680000, 1), new(24, 20)]));
        }
        string identities = JsonSerializer.Serialize(fixture.Query("SELECT item_uid,json_remove(item_core,'$.SealFlag') FROM character_inventory_items ORDER BY item_uid"));
        byte[]? wornAvatar = null, wornPet = null;
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            var details = peer.SpecialContainers[0].Body;
            using (var reader = new BinaryReader(new MemoryStream(details)))
            {
                That(reader.ReadByte() == 2, "选角在Player之前下发两只宠物详情");
                var petUids = new HashSet<int>();
                for (int index = 0; index < 2; index++)
                {
                    That(petUids.Add(reader.ReadInt32()), "同模板宠物详情保留独立UID");
                    That(reader.ReadByte() == 100 && reader.ReadByte() == 0 && reader.ReadInt32() == 0 &&
                        reader.ReadByte() == 1, "初始宠物详情保留饱食度、模式、经验和等级");
                    string name = CharacterName.Encoding.GetString(reader.ReadBytes(reader.ReadInt32()));
                    That(defaultPetName.Length > 0 && name == defaultPetName && reader.ReadByte() == 0,
                        "新授予宠物详情携带非空默认名称与完整尾标志");
                }
                That(reader.BaseStream.Position == reader.BaseStream.Length, "宠物详情名称变长后两条记录仍完整对齐");
            }
            int petUid = checked((int)U32(details, 1));
            That(peer.SpecialContainers[1].Body.Length == 335 && U16(peer.SpecialContainers[1].Body, 3) == 2 &&
                peer.SpecialContainers[2].Body.Length == 495, "both dedicated lists initialized including artifacts and feed");
            PetMoves(peer);
            wornAvatar = peer.SpecialContainers[1].Body.AsSpan(5, 165).ToArray();
            peer.Send(19, Move(1, 0, 101550507, 3, 0, 0));
            peer.Expect(1, 19, Ack(1, 0, 3, 0));
            InventoryDeltaTestChecks.Read(peer, 1, 0, 0);
            var presetSync = peer.Expect(0, 1599).Body;
            That(presetSync.Length == 49 && presetSync[0] == 0 && presetSync[5] == 1,
                "穿戴变化实时回写1号橱窗：0x063F单条在Actor之前补发且部位数为1");
            peer.Expect(0, 2);
            That(peer.Expect(0, 2).Body[126] == 1, "avatar ACK followed by Player carries worn avatar without generic Equipment delta");
            peer.Send(19, Move(7, 0, 63000, 3, 26, 0));
            peer.Expect(1, 19, Ack(7, 0, 3, 26));
            peer.Expect(0, 105, details);
            wornPet = peer.Expect(0, 14).Body;
            That(wornPet.Length == 126 && wornPet[0] == 3 && U32(wornPet, 9) == petUid, "pet equip sends linked identity before Player");
            InventoryDeltaTestChecks.Read(peer, 7, 0, 0);
            CreatureActor(peer.Expect(0, 2).Body, 63000, defaultPetName, "穿戴提交后的Actor创建当前宠物实体");
            peer.Expect(0, 2);
            peer.Send(19, Move(7, 140, 2680000, 3, 28, 0));
            peer.Expect(1, 19, Ack(7, 140, 3, 28));
            That(peer.Expect(0, 14).Body.Length == 126, "pet artifact uses ordinary core at Client slot28");
            InventoryDeltaTestChecks.Read(peer, 7, 140, 0);
            peer.Expect(0, 2); peer.Expect(0, 2);
            peer.Barrier();
            That(JsonSerializer.Serialize(fixture.Query("SELECT item_uid,json_remove(item_core,'$.SealFlag') FROM character_inventory_items ORDER BY item_uid")) == identities,
                "TCP equip preserves UID and core fields other than artifact unsealing");
        }
        using (var peer = Open(fixture))
        {
            var packets = peer.Select();
            CreatureActor(packets[2].Body, 63000, defaultPetName, "重连Actor恢复原穿戴宠物模板");
            That(peer.SpecialContainers[0].Body[0] == 2 && U16(peer.SpecialContainers[1].Body, 3) == 1 && U16(peer.SpecialContainers[2].Body, 1) == 2,
                "reconnect retains worn special items without duplicating bag entries");
            That(packets[3].Body[126] == 3, "reconnect Player includes avatar, pet and pet equipment together");
            peer.Send(19, Move(1, 2, 0, 3, 0, 101550507));
            peer.Expect(1, 19, Ack(1, 2, 3, 0));
            var avatar = InventoryDeltaTestChecks.Read(peer, 1, 2, 101550507);
            Bytes(avatar.AsSpan(5, 163), wornAvatar!.AsSpan(2), "时装卸下的局部差量保留原165B核心");
            var presetCleared = peer.Expect(0, 1599).Body;
            That(presetCleared is [0, 0, 0, 0, 0, 0], "卸下全部时装后1号橱窗同步为空部位");
            CreatureActor(peer.Expect(0, 2).Body, 63000, defaultPetName, "时装脱下不会清空仍穿戴的宠物");
            peer.Expect(0, 2);
            peer.Send(19, Move(7, 5, 0, 3, 26, 63000));
            peer.Expect(1, 19, Ack(7, 5, 3, 26));
            peer.Expect(0, 105); peer.Expect(0, 14);
            var pet = InventoryDeltaTestChecks.Read(peer, 7, 5, 63000);
            Bytes(pet.AsSpan(5, 121), wornPet!.AsSpan(5), "宠物卸下的局部差量保留完整核心");
            CreatureActor(peer.Expect(0, 2).Body, 0, "", "宠物脱下提交后的Actor清除实体");
            peer.Expect(0, 2);
            peer.Barrier();
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_special_tcp BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'special failure'); END");
            peer.Send(19, Move(1, 2, 101550507, 3, 0, 0));
            peer.Closed();
            That(Snapshot() == before, "late TCP failure publishes no success and rolls back avatar core, detail and position");
            fixture.Sql("DROP TRIGGER fail_special_tcp");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            That(U16(peer.SpecialContainers[1].Body, 3) == 2 && U16(peer.SpecialContainers[2].Body, 1) == 3, "failed move remains rolled back after reconnect");
            peer.Barrier();
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "special-container TCP fixture has no orphan details or foreign-key violations");

        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_slots ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM inventory_avatar_details ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM inventory_creature_details ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT revision FROM characters"));
    }

    static void PetMoves(TcpPeer peer)
    {
        foreach (var row in new (short From, short To, int Id, int Other, int Count)[]
            { (0, 5, 63000, 0, 1), (5, 1, 63000, 63000, 1), (5, 1, 63000, 63000, 1), (5, 0, 63000, 0, 1),
              (140, 141, 2680000, 0, 1), (141, 140, 2680000, 0, 1), (189, 191, 24, 0, 20), (191, 189, 24, 0, 20) })
        {
            peer.Send(19, Move(7, row.From, row.Id, 7, row.To, row.Other, row.Count));
            peer.Expect(1, 19, Ack(7, row.From, 7, row.To, row.Count));
            peer.Barrier(packet => That(packet.Opcode is not (13 or 14 or 105 or 1681),
                "宠物、装备及饲料普通整件换格与同模板不同实例交换只回ACK，不重新载入图标或详情"));
        }
        peer.Send(19, Move(7, 189, 24, 7, 190, 0, 5));
        peer.Expect(1, 19, Ack(7, 189, 7, 190, 5));
        Feed(peer.Expect(0, 13).Body, 189, 15, 190, 5);
        peer.Barrier(packet => That(packet.Opcode is not (14 or 105 or 1681), "宠物饲料拆分以无动画全量同步数量"));
        peer.Send(19, Move(7, 190, 24, 7, 189, 24, 5));
        peer.Expect(1, 19, Ack(7, 190, 7, 189, 5));
        Bytes(peer.Expect(0, 14).Body.AsSpan(0, 9), [7, 1, 0, 190, 0, 255, 255, 255, 255], "饲料合并只清空耗尽的源格，不正数回填获得effect");
        Feed(peer.Expect(0, 13).Body, 189, 20);
        peer.Barrier(packet => That(packet.Opcode is not (14 or 105 or 1681), "饲料合并后的正数数量只用全量恢复"));

        static void Feed(byte[] body, short first, uint count, short second = -1, uint secondCount = 0)
        {
            int items = second < 0 ? 4 : 5;
            That(body[0] == 7 && U16(body, 1) == items && body.Length == 3 + 123 * items,
                "宠物数量同步保留完整容器及123B核心");
            var slots = Enumerable.Range(0, items).Select(i => 3 + 123 * i).ToArray();
            int offset = slots.Single(i => U16(body, i) == first);
            That(U32(body, offset + 2) == 24 && U32(body, offset + 6) == count, "饲料主堆的绝对数量正确");
            if (second >= 0)
            {
                offset = slots.Single(i => U16(body, i) == second);
                That(U32(body, offset + 2) == 24 && U32(body, offset + 6) == secondCount, "饲料分堆的绝对数量正确");
            }
            else
                That(!slots.Any(i => U16(body, i) == 190), "合并后不恢复已清空源格");
        }
    }

    static void CreatureActor(byte[] body, uint template, string expectedName, string message)
    {
        const int nameWidth = 10; // SpecialTcp的CP936字节数。
        That(body[132 + nameWidth] == 0, "城镇宠物Actor保持成员状态0，不混入副本加载状态");
        int offset = 156 + nameWidth + body[133 + nameWidth] * 25;
        int creatureNameWidth = checked((int)U32(body, offset + 4));
        That(body[0] == 0 && U32(body, offset) == template &&
            CharacterName.Encoding.GetString(body, offset + 8, creatureNameWidth) == expectedName &&
            body[offset + 8 + creatureNameWidth] == (template == 0 ? 0 : 1), message + "；同步模板、名称与启用标志");
    }

    static byte[] Move(byte fromList, short fromSlot, int fromId, byte toList, short toSlot, int toId, int count = 1) => Build(writer =>
    {
        writer.Write(fromList); writer.Write(fromSlot); writer.Write(fromId); writer.Write(count);
        writer.Write(toList); writer.Write(toSlot); writer.Write(toId); writer.Write(0); writer.Write(-1); writer.Write((ushort)0);
    });
    static byte[] Ack(byte fromList, short fromSlot, byte toList, short toSlot, int count = 1) => Build(writer =>
    {
        writer.Write((byte)1); writer.Write(fromList); writer.Write(fromSlot); writer.Write(count); writer.Write(toList); writer.Write(toSlot);
    });
}
