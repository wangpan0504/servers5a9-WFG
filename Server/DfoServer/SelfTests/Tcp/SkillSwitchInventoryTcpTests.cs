using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class SkillSwitchInventoryTcpTests
{
    const ushort Command = 0x0595;
    const ushort DeltaNotice = 0x055C;
    const ushort AllDataNotice = 0x055D;

    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await WindowRefreshRegressionAsync(source, root, resources);
        var support = SwitchingSupportSkillCatalog.Load(resources.Archive);
        var profile = FindProfile(support);
        ItemTemplate template = resources.Characters.Get(profile.Job).Equipment
            .Select(row => row.Template)
            .First(row => row.EquipmentSlot is >= 12 and <= 25);
        var item = InventoryRewardGrantService.CreateItem(
            template,
            new(InventoryListType.Main, 9),
            InventoryRewardGrantService.Create(template));
        await using var fixture = new TcpTestFixture(
            source,
            Path.Combine(root, "skill-switch-inventory"),
            resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        Character created;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            created = repository.Create(owner, new("SwitchTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = profile.Job,
                GrowType = profile.Grow,
                Level = 95,
                AcceptedStoryLevel = 1,
                Skills = resources.Characters.Get(profile.Job).Skills
                    .Where(skill => skill.Id != profile.Skill && skill.Id != profile.Second).ToArray(),
                Inventory = new([item])
            });
        InventoryItem sourceItem = created.Inventory.Items.Single();
        short sourceSlot = sourceItem.Position.Index;
        short movedSlot = sourceSlot == 9 ? (short)10 : (short)9;
        byte part = template.EquipmentSlot;
        int quality = sourceItem.Core.Value;

        await fixture.StartAsync();
        long selectedRevision;
        using (var peer = Open(fixture))
        {
            string beforeSelection = Snapshot();
            peer.Send(Command, Request(profile.Skill, 36, 47, ushort.MaxValue));
            peer.Expect(1, Command, []);
            That(Snapshot() == beforeSelection,
                "尚未选角的登记只回空ACK，不发登记快照且不写库");
            var entry = peer.Select();
            var initial = ReadUserInfoSection(entry[3].Body);
            That(initial.SkillId == 0 && initial.Items.Length == 0,
                "首次入城USERINFO携带空Buff强化登记段");
            selectedRevision = Convert.ToInt64(fixture.Query(
                "SELECT revision FROM characters WHERE id=$p0", created.Id)[0][0]);

            peer.Send(Command, Request(profile.Skill, 36, 47, ushort.MaxValue));
            peer.Expect(1, Command, []);
            peer.Expect(0, AllDataNotice, Build(writer =>
            {
                writer.Write(profile.Skill);
                writer.Write((byte)0);
            }));

            string learnedBefore = fixture.Snapshot(
                "SELECT * FROM character_skills WHERE character_id=$p0 ORDER BY skill_id", created.Id);
            That(fixture.Query("SELECT count(*) FROM character_skills WHERE character_id=$p0 AND skill_id IN ($p1,$p2)",
                created.Id, profile.Skill, profile.Second) is [[0L]], "多候选TCP角色尚未学习两个目标Buff");
            foreach (ushort chosen in new[] { profile.Second, profile.Skill, (ushort)0, profile.Skill })
            {
                peer.Send(Command, Request(chosen, 36, 47, ushort.MaxValue));
                peer.Expect(1, Command, []);
                peer.Expect(0, AllDataNotice, Build(writer => { writer.Write(chosen); writer.Write((byte)0); }));
            }
            peer.Barrier();
            That(fixture.Snapshot("SELECT * FROM character_skills WHERE character_id=$p0 ORDER BY skill_id", created.Id) == learnedBefore,
                "未学候选选择、切换及取消只更新偏好，不新增或提高实际技能");

            peer.Send(Command, Request(profile.Skill, part, 0, checked((ushort)sourceSlot)));
            peer.Expect(1, Command, []);
            Delta(peer.Expect(0, DeltaNotice).Body, part, 0, checked((ushort)sourceSlot), present: true);
            That(fixture.Query("""
                SELECT s.skill_id||':'||i.equipment_part||':'||i.item_id
                FROM character_skill_switch_inventory s
                JOIN character_skill_switch_inventory_items i USING(character_id)
                WHERE s.character_id=$p0
                """, created.Id) is [[string persisted]] &&
                persisted == $"{profile.Skill}:{part}:{template.Id}",
                "技能与稳定物品绑定在命令成功后持久化");

            long registeredRevision = Convert.ToInt64(fixture.Query(
                "SELECT revision FROM characters WHERE id=$p0", created.Id)[0][0]);
            peer.Send(Command, Request(profile.Skill, part, 0, checked((ushort)sourceSlot)));
            peer.Expect(1, Command, []);
            Delta(peer.Expect(0, DeltaNotice).Body, part, 0, checked((ushort)sourceSlot), present: true);
            peer.Send(Command, Request(profile.Skill, part, 17, part));
            peer.Expect(1, Command, []);
            Delta(peer.Expect(0, DeltaNotice).Body, part, 0, checked((ushort)sourceSlot), present: true);
            peer.Barrier();
            That(Convert.ToInt64(fixture.Query(
                "SELECT revision FROM characters WHERE id=$p0", created.Id)[0][0]) == registeredRevision,
                "重复登记与无权威容器的合法业务拒绝只同步当前事实，不增加revision");

            peer.Send(19, MoveItem(sourceSlot, template.Id, movedSlot));
            peer.Expect(1, 19, MoveAck(sourceSlot, movedSlot));
            Delta(peer.Expect(0, DeltaNotice).Body, part, 0, checked((ushort)movedSlot), present: true);
            peer.Barrier();
            That(fixture.Query("""
                SELECT s.slot_index||':'||b.item_uid
                FROM character_inventory_slots s
                JOIN character_skill_switch_inventory_items b USING(item_uid)
                WHERE b.character_id=$p0
                """, created.Id) is [[string moved]] &&
                moved.StartsWith(movedSlot + ":", StringComparison.Ordinal),
                "普通背包移动保留UID绑定并发布登记新位置");

            peer.Send(19, MoveItem(movedSlot, template.Id, 0, targetList: 2));
            peer.Expect(1, 19, MoveAck(movedSlot, 0, targetList: 2));
            That(peer.Expect(0, 14).Body[0] == 0 && peer.Expect(0, 14).Body[0] == 2,
                "存仓先发布携带源清除和仓库真实物品");
            Delta(peer.Expect(0, DeltaNotice).Body, part, 47, ushort.MaxValue, present: false);
            peer.Barrier();
            That(fixture.Query("SELECT item_uid FROM character_skill_switch_inventory_items WHERE character_id=$p0",
                created.Id) is [[long storedUid]] && storedUid == sourceItem.Uid,
                "存仓即时停止有效登记投影，但保留同角色原UID配置");
            string beforeCargoRequest = Snapshot();
            peer.Send(Command, Request(profile.Skill, part, 2, 0));
            peer.Expect(1, Command, []);
            Delta(peer.Expect(0, DeltaNotice).Body, part, 47, ushort.MaxValue, present: false);
            peer.Send(Command, Request(profile.Skill, 36, 47, ushort.MaxValue));
            peer.Expect(1, Command, []);
            peer.Expect(0, AllDataNotice, Build(writer =>
            {
                writer.Write(profile.Skill);
                writer.Write((byte)0);
            }));
            peer.Barrier();
            That(Snapshot() == beforeCargoRequest,
                "仓库新登记拒绝及重复指定技能不写库，全量也不公布仓库绑定");

            peer.Send(7);
            peer.Expect(1, 7, [1]);
            peer.Expect(0, 2);
            string beforeLateRequest = Snapshot();
            peer.Send(Command, Request(ushort.MaxValue, 36, 47, ushort.MaxValue));
            peer.Expect(1, Command, []);
            That(Snapshot() == beforeLateRequest,
                "返回选角后的迟到登记不清除原绑定、不增加revision且不发1372/1373");
            var reselected = ReadUserInfoSection(peer.Select()[3].Body);
            That(reselected.SkillId == profile.Skill && reselected.Items.Length == 0,
                "迟到登记拒绝后重新选角保留技能，但不回放仓库绑定为有效装备");
            peer.Barrier();
        }

        await fixture.StopAsync();
        await fixture.StartAsync("skill-switch-restart", isolatedProcess: true);
        using (var peer = Open(fixture))
        {
            var restored = ReadUserInfoSection(peer.Select()[3].Body);
            That(restored.SkillId == profile.Skill && restored.Items.Length == 0,
                "重启后USERINFO仍不把仓库中的登记项投影为有效装备");

            peer.Send(19, MoveItem(0, template.Id, movedSlot, sourceList: 2));
            peer.Expect(1, 19, MoveAck(0, movedSlot, sourceList: 2));
            That(peer.Expect(0, 14).Body[0] == 0 && peer.Expect(0, 14).Body[0] == 2,
                "取回发布真实库存差量，不复制登记物品");
            Delta(peer.Expect(0, DeltaNotice).Body, part, 0, checked((ushort)movedSlot), present: true);
            peer.Barrier();
            That(fixture.Query("SELECT item_uid FROM character_skill_switch_inventory_items WHERE character_id=$p0",
                created.Id) is [[long returnedUid]] && returnedUid == sourceItem.Uid,
                "取回同一UID即时恢复有效登记，重启未丢失配置");

            peer.Send(7);
            peer.Expect(1, 7, [1]);
            peer.Expect(0, 2);
            var carried = ReadUserInfoSection(peer.Select()[3].Body);
            That(carried.SkillId == profile.Skill && carried.Items is
                [{ List: 0, Slot: var carriedSlot, ItemId: var carriedId }] &&
                carriedSlot == movedSlot && carriedId == template.Id,
                "取回后重新选角完整回放原技能及携带登记项，保留正向USERINFO覆盖");
            peer.Barrier();

            peer.Send(18, DeleteItem(movedSlot, quality));
            peer.Expect(1, 18);
            peer.Expect(0, 14);
            Delta(peer.Expect(0, DeltaNotice).Body, part, 47, ushort.MaxValue, present: false);
            peer.Barrier();
            That(fixture.Query(
                "SELECT count(*) FROM character_skill_switch_inventory_items WHERE character_id=$p0",
                created.Id) is [[0L]] &&
                fixture.Query(
                "SELECT skill_id FROM character_skill_switch_inventory WHERE character_id=$p0",
                created.Id) is [[long skill]] && skill == profile.Skill,
                "删除库存实例在同事务清除绑定并保留指定技能");

            string beforeFailure = Snapshot();
            fixture.Sql("""
                CREATE TRIGGER fail_skill_switch_clear
                BEFORE DELETE ON character_skill_switch_inventory
                BEGIN SELECT RAISE(ABORT,'skill switch rollback'); END
                """);
            peer.Send(Command, Request(ushort.MaxValue, 36, 47, ushort.MaxValue));
            peer.Closed();
            That(Snapshot() == beforeFailure,
                "登记持久化晚期失败回滚角色revision与原指定技能，不提前发送成功");
            fixture.Sql("DROP TRIGGER fail_skill_switch_clear");
        }

        using (var peer = Open(fixture))
        {
            var retained = ReadUserInfoSection(peer.Select()[3].Body);
            That(retained.SkillId == profile.Skill && retained.Items.Length == 0,
                "失败后重连仍恢复原指定技能与已清理物品集合");
            peer.Send(Command, Request(ushort.MaxValue, 36, 47, ushort.MaxValue));
            peer.Expect(1, Command, []);
            peer.Expect(0, AllDataNotice, [0, 0, 0]);
            peer.Barrier();
            That(fixture.Query(
                "SELECT count(*) FROM character_skill_switch_inventory WHERE character_id=$p0",
                created.Id) is [[0L]],
                "清除最后指定技能删除空状态行");
        }

        using (var peer = Open(fixture))
        {
            peer.Select();
            string beforeMalformed = Snapshot();
            peer.Send(Command, [0, 0, 36, 47, 0xFF]);
            peer.Closed();
            That(Snapshot() == beforeMalformed,
                "非六字节请求关闭连接且不写入登记或角色revision");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] &&
            fixture.Query("PRAGMA foreign_key_check").Length == 0 &&
            Convert.ToInt64(fixture.Query(
                "SELECT revision FROM characters WHERE id=$p0", created.Id)[0][0]) == selectedRevision + 11,
            "登记、移动、存取仓库、删除和清除各提交一次，零变化与失败不写库且外键完整");

        string Snapshot() =>
            fixture.Snapshot("SELECT id,revision FROM characters WHERE id=$p0", created.Id) +
            fixture.Snapshot("SELECT * FROM character_skill_switch_inventory WHERE character_id=$p0", created.Id) +
            fixture.Snapshot("SELECT * FROM character_skill_switch_inventory_items WHERE character_id=$p0 ORDER BY equipment_part", created.Id) +
            fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", created.Id) +
            fixture.Snapshot("SELECT list_type,slot_index,item_uid FROM character_inventory_slots WHERE character_id=$p0 ORDER BY list_type,slot_index", created.Id);
    }

    static async Task WindowRefreshRegressionAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var profile = FindProfile(SwitchingSupportSkillCatalog.Load(resources.Archive));
        var avatarTemplate = resources.ItemResources.Items.Get(resources.Characters.Get(profile.Job).DefaultAvatarEquipment[3]);
        var avatar = InventoryRewardGrantService.CreateItem(avatarTemplate, new(InventoryListType.Avatar, 0),
            InventoryRewardGrantService.Create(avatarTemplate)) with
            { Avatar = new() { Socket0 = new(1, 3333001), Color1 = 7, Color2 = 8 } };
        var weaponTemplate = resources.Characters.Get(profile.Job).Equipment.Single(row => row.Slot == 12).Template;
        var weapon = InventoryRewardGrantService.CreateItem(weaponTemplate, new(InventoryListType.Main, 9),
            InventoryRewardGrantService.Create(weaponTemplate) with
            { SealFlag = 0, Attr = 7, EnchantCardId = 10309006, EnchantUpgrade = 2, AmplifyType = 1, AmplifyValue = 55, GenuineUpgrade = 3, Rune = 3 });
        var clone = new InventoryItem(0, new(InventoryListType.Avatar, 1), new(113590006, ItemKind.Avatar, 0))
            { Avatar = new() { Socket0 = new(2, 3333002) } };
        var aura = new InventoryItem(0, new(InventoryListType.Avatar, 2), new(101590068, ItemKind.Avatar, 0))
            { Avatar = new() { Color1 = 7, Color2 = 8 } };
        var pet = new InventoryItem(0, new(InventoryListType.Pet, 0), new(63032, ItemKind.Creature, 0))
            { Creature = new() { Name = "BuffPet", Satiety = 73 } };
        var otherPet = new InventoryItem(0, new(InventoryListType.Pet, 1), new(63023, ItemKind.Creature, 0))
            { Creature = new() { Name = "OtherPet", Satiety = 60 } };
        var artifacts = new[] { 63500, 63501, 64000, 64001, 64500, 64501 }
            .Select((id, index) => new InventoryItem(0, new(InventoryListType.Pet, (short)(140 + index)),
                new(id, ItemKind.CreatureEquipment, 999999998) { Attr = (byte)(index + 1) })).ToArray();
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "buff-window-refresh"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        Character created;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            created = repository.Create(owner, new("BuffWindow", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = profile.Job, GrowType = profile.Grow, Level = 95, AcceptedStoryLevel = 1,
                Skills = resources.Characters.Get(profile.Job).Skills,
                Inventory = new([avatar, pet, otherPet, weapon, clone, aura, .. artifacts])
            });
        await fixture.StartAsync();
        using var peer = Open(fixture);
        peer.Select();
        peer.Barrier();
        peer.Send(Command, Request(profile.Skill, 36, 47, ushort.MaxValue));
        peer.Expect(1, Command, []);
        peer.Expect(0, AllDataNotice, Build(writer => { writer.Write(profile.Skill); writer.Write((byte)0); }));
        int closingPlayers = 0;
        string skills = fixture.Snapshot("SELECT * FROM character_skills WHERE character_id=$p0 ORDER BY skill_id", created.Id);
        foreach (var row in new[] { (List: (byte)1, Part: (byte)3, Item: avatarTemplate.Id),
            (List: (byte)7, Part: (byte)26, Item: 63032),
            (List: (byte)0, Part: (byte)12, Item: weaponTemplate.Id) })
        {
            short bag = row.List == 0 ? (short)9 : (short)0;
            peer.Send(Command, Request(profile.Skill, row.Part, row.List, checked((ushort)bag)));
            peer.Expect(1, Command, []);
            Delta(peer.Expect(0, DeltaNotice).Body, row.Part, row.List, checked((ushort)bag), present: true);
            peer.Barrier();
            string bindings = fixture.Snapshot("SELECT * FROM character_skill_switch_inventory_items WHERE character_id=$p0 ORDER BY equipment_part", created.Id);
            foreach (bool wear in new[] { true, false })
            {
                byte from = wear ? row.List : (byte)3, to = wear ? (byte)3 : row.List;
                short fromSlot = wear ? bag : row.Part, toSlot = wear ? row.Part : bag;
                // 取消选择后装备登记仍在，纯移动同样不能重建窗口。
                if (!wear)
                {
                    peer.Send(Command, Request(0, 36, 47, ushort.MaxValue));
                    peer.Expect(1, Command, []);
                    var cleared = peer.Expect(0, AllDataNotice).Body;
                    That(U16(cleared, 0) == 0 && cleared[2] > 0, "取消选择立即同步零技能并保留已有装备登记");
                    peer.Barrier();
                }
                peer.Send(19, MoveItem(fromSlot, row.Item, toSlot, from, to));
                peer.Expect(1, 19, MoveAck(fromSlot, toSlot, from, to));
                int deltaCount = 0, equipmentCount = 0, actorCount = 0, creatureDetails = 0;
                peer.Barrier(packet =>
                {
                    That(packet.Opcode is 2 or 13 or 14 or 105 or 1372 or 1449 or 1599 or 1681,
                        "普通穿脱只发布库存、外观及登记同步，不发送未知窗口通知");
                    if (packet.Opcode == 13)
                        That(packet.Body[0] is 0 or 1 or 7, "登记中的穿脱只全量重载本次包裹，跳过新物品标记");
                    if (packet.Opcode is 14 or 1681 && packet.Body.Length > 0 && packet.Body[0] is 0 or 1 or 7)
                        That(false, "包裹侧不再用14/1681回填已有物品");
                    if (packet.Opcode == 2 && packet.Body[0] == 1)
                        closingPlayers++;
                    if (packet.Opcode == 2 && packet.Body[0] == 0)
                    {
                        actorCount++;
                        That(packet.Body[132 + NameWidth("BuffWindow")] == 0, "城镇换装Actor保持城镇成员状态");
                        if (row.Part == 26)
                        {
                            int petAt = 156 + NameWidth("BuffWindow") + packet.Body[133 + NameWidth("BuffWindow")] * 25;
                            That(U32(packet.Body, petAt) == (wear ? 63032u : 0u), "Actor仍声明实际宠物模板或清除卸下实体");
                        }
                    }
                    if (packet.Opcode == 105)
                    {
                        creatureDetails++;
                        That(packet.Body[0] == 2 && packet.Body[5] == 73, "宠物完整详情在装备差量前恢复原饱食与UID");
                    }
                    if (packet.Opcode == 14 && packet.Body[0] == 3)
                    {
                        equipmentCount++;
                        That(U16(packet.Body, 1) == 1 && U16(packet.Body, 3) == row.Part &&
                            U32(packet.Body, 5) == (wear ? (uint)row.Item : uint.MaxValue), "穿戴专属完整差量覆盖实际穿脱槽");
                        if (wear && row.Part == 3)
                            That(packet.Body.Length == 168 && U32(packet.Body, 126) == 30 &&
                                U16(packet.Body, 130) == 1 && U32(packet.Body, 132) == 3333001 &&
                                U32(packet.Body, 160) == 4 && U16(packet.Body, 164) == 7 && U16(packet.Body, 166) == 8,
                                "不依赖Player仍完整同步五孔位RAW30与染色RAW4");
                        if (wear && row.Part == 12)
                            That(packet.Body.Length == 126 && packet.Body[13] == 7 && U32(packet.Body, 17) == 10309006 &&
                                packet.Body[21] == 2 && packet.Body[22] == 1 && U16(packet.Body, 23) == 55 &&
                                U16(packet.Body, 72) == 3 && packet.Body[90] == 3,
                                "完整123B差量保留强化、附魔、增幅、符文与锻造");
                        if (row.Part == 26 && wear)
                            That(creatureDetails == 1, "先到宠物详情再安装穿戴实例");
                    }
                    if (packet.Opcode == DeltaNotice)
                    {
                        Delta(packet.Body, row.Part, to, checked((ushort)toSlot), present: true);
                        deltaCount++;
                    }
                });
                That(deltaCount == 1 && fixture.Snapshot("SELECT * FROM character_skill_switch_inventory_items WHERE character_id=$p0 ORDER BY equipment_part", created.Id) == bindings,
                    "穿脱只更新登记投影的位置，同一UID登记保持不变");
                That(equipmentCount == 1 && actorCount == 1, "完整装备差量和外观声明各发布一次，替代登记重建");
                if (!wear)
                {
                    peer.Send(Command, Request(profile.Skill, 36, 47, ushort.MaxValue));
                    peer.Expect(1, Command, []);
                    byte[] restored = peer.Expect(0, AllDataNotice).Body;
                    That(U16(restored, 0) == profile.Skill && restored[2] > 0, "重新勾选立即恢复目标技能及全部有效绑定");
                    peer.Barrier();
                }
            }
        }
        foreach (var move in new[] { (Bag: (short)0, Id: pet.Core.ItemId, Target: 0),
            (Bag: (short)1, Id: otherPet.Core.ItemId, Target: pet.Core.ItemId),
            (Bag: (short)1, Id: pet.Core.ItemId, Target: otherPet.Core.ItemId) })
        {
            peer.Send(19, MoveItem(move.Bag, move.Id, 26, 7, 3, move.Target));
            peer.Expect(1, 19, MoveAck(move.Bag, 26, 7, 3));
            int equipmentCount = 0;
            peer.Barrier(packet =>
            {
                if (packet.Opcode == 14 && packet.Body[0] == 3)
                {
                    equipmentCount++;
                    That(U16(packet.Body, 1) == 1 && U16(packet.Body, 3) == 26 && U32(packet.Body, 5) == move.Id,
                        "宠物互换后只同步实际穿戴实例，不先清空宠物对象");
                }
                That(packet.Opcode != 2 || packet.Body[0] == 0, "宠物互换不重建Buff登记窗口");
            });
            That(equipmentCount == 1, "宠物互换发送一次完整穿戴差量");
        }
        for (int part = 27; part <= 29; part++)
        {
            int index = 2 * (part - 27);
            var first = artifacts[index];
            var second = artifacts[index + 1];
            foreach (var move in new[] { (Bag: first.Position.Index, Item: first, Target: 0),
                (Bag: second.Position.Index, Item: second, Target: first.Core.ItemId),
                (Bag: second.Position.Index, Item: first, Target: second.Core.ItemId),
                (Bag: first.Position.Index, Item: (InventoryItem?)null, Target: first.Core.ItemId) })
            {
                peer.Send(19, MoveItem(move.Bag, move.Item?.Core.ItemId ?? 0, (short)part, 7, 3, move.Target));
                peer.Expect(1, 19, MoveAck(move.Bag, (short)part, 7, 3));
                int equipmentCount = 0;
                peer.Barrier(packet =>
                {
                    if (packet.Opcode == 14 && packet.Body[0] == 3)
                    {
                        equipmentCount++;
                        That(U16(packet.Body, 1) == 1 && U16(packet.Body, 3) == part &&
                            U32(packet.Body, 5) == (move.Item is null ? uint.MaxValue : (uint)move.Item.Core.ItemId),
                            "宠物装备覆盖不清空穿戴对象，真正卸下才发送空槽 " + part);
                        if (move.Item is not null)
                            That(packet.Body.Length == 126 && packet.Body[13] == move.Item.Core.Attr,
                                "宠物装备完整差量保持实际强化属性 " + part);
                    }
                    That(packet.Opcode != 2 || packet.Body[0] == 0, "宠物装备穿脱不重建Buff登记窗口");
                });
                That(equipmentCount == 1, "每次宠物装备操作只发布一次穿戴差量 " + part);
            }
        }
        // 克隆在已穿原件上覆盖，来源UID/颜色由本次事务决定；卸下再穿空槽解除外观。
        foreach (var move in new[] { (Bag: (short)2, Id: aura.Core.ItemId, Target: 0),
            (Bag: (short)1, Id: clone.Core.ItemId, Target: aura.Core.ItemId),
            (Bag: (short)1, Id: aura.Core.ItemId, Target: clone.Core.ItemId),
            (Bag: (short)1, Id: clone.Core.ItemId, Target: aura.Core.ItemId),
            (Bag: (short)2, Id: 0, Target: clone.Core.ItemId),
            (Bag: (short)2, Id: clone.Core.ItemId, Target: 0) })
        {
            peer.Send(19, MoveItem(move.Bag, move.Id, 9, 1, 3, move.Target));
            peer.Expect(1, 19, MoveAck(move.Bag, 9, 1, 3));
            bool cloneWorn = move.Id == clone.Core.ItemId;
            int actors = 0, equipmentCount = 0;
            peer.Barrier(packet =>
            {
                That(packet.Kind == 0 && (packet.Opcode != 2 || packet.Body[0] == 0), "克隆覆盖、卸下及穿回同样不得发布本人Player");
                if (packet.Opcode == 2)
                {
                    actors++;
                    if (cloneWorn)
                    {
                        int at = 134 + NameWidth("BuffWindow");
                        int appearance = move.Target == aura.Core.ItemId ? aura.Core.ItemId : 0;
                        That(packet.Body[at] == 9 && U32(packet.Body, at + 1) == clone.Core.ItemId &&
                            U32(packet.Body, at + 16) == appearance && U16(packet.Body, at + 9) == (appearance == 0 ? 0 : 7),
                            "Actor投影真实克隆模板：覆盖原件借用外观染色，脱下重穿空槽清零");
                    }
                }
                if (packet.Opcode == 14 && packet.Body[0] == 3)
                {
                    equipmentCount++;
                    That(U16(packet.Body, 1) == 1 && U16(packet.Body, 3) == 9 &&
                        U32(packet.Body, 5) == (move.Id == 0 ? uint.MaxValue : (uint)move.Id),
                        "光环覆盖交换只更新ACK19已安装的实例，不先清空穿戴对象；实际卸下才发送空槽");
                    if (cloneWorn)
                        That(U16(packet.Body, 130) == 2 && U32(packet.Body, 132) == 3333002,
                            "克隆自身孔位来自完整实例差量，不错误借用原件孔位");
                }
            });
            That(actors == 1 && equipmentCount == 1, "光环移动保留一次完整穿戴差量和本人Actor外观同步");
        }
        That(fixture.Snapshot("SELECT * FROM character_skills WHERE character_id=$p0 ORDER BY skill_id", created.Id) == skills,
            "穿脱和勾选只影响库存位置与偏好，不改变已学技能或等级");
        Console.WriteLine($"#802 avatar/pet wear/unwear: closing Player packets={closingPlayers}");
        That(closingPlayers == 0, "Buff窗口打开期间的时装/宠物穿脱不得发送重建登记并关闭窗口的本人Player");
    }

    static (byte Job, byte Grow, ushort Skill, ushort Second) FindProfile(SwitchingSupportSkillCatalog support)
    {
        for (byte job = 0; job < 16; job++)
        for (byte grow = 0; grow <= 5; grow++)
        {
            ushort[] skills = support.Skills(job, grow).Order().ToArray();
            if (skills.Length > 1 && SkillPointLedger.SupportsGrowth(grow, job))
                return (job, grow, skills[0], skills[1]);
        }
        throw new InvalidDataException("当前PVF没有可执行未学多候选Buff预设TCP回归的职业");
    }

    static byte[] Request(ushort skill, byte part, byte list, ushort slot) => Build(writer =>
    {
        writer.Write(skill);
        writer.Write(part);
        writer.Write(list);
        writer.Write(slot);
    });

    static byte[] MoveItem(short source, int itemId, short destination, byte sourceList = 0, byte targetList = 0, int targetItemId = 0) => Build(writer =>
    {
        writer.Write(sourceList);
        writer.Write(source);
        writer.Write(itemId);
        writer.Write(1);
        writer.Write(targetList);
        writer.Write(destination);
        writer.Write(targetItemId);
        writer.Write(0);
        writer.Write(-1);
        writer.Write((ushort)0);
    });

    static byte[] MoveAck(short source, short destination, byte sourceList = 0, byte targetList = 0) => Build(writer =>
    {
        writer.Write((byte)1);
        writer.Write(sourceList);
        writer.Write(source);
        writer.Write(1);
        writer.Write(targetList);
        writer.Write(destination);
    });

    static byte[] DeleteItem(short slot, int value)
    {
        byte[] child = Build(writer =>
        {
            writer.Write(new byte[] { 8, 0, 16, checked((byte)slot), 24 });
            Varint(writer, checked((uint)value));
            writer.Write(new byte[] { 32, 1 });
        });
        byte[] payload = Build(writer =>
        {
            writer.Write(new byte[] { 8, 18, 16, 0, 26, checked((byte)child.Length) });
            writer.Write(child);
            writer.Write(new byte[] { 32, 0 });
        });
        return Build(writer =>
        {
            writer.Write(payload.Length);
            writer.Write(payload);
        });
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

    static void Delta(byte[] body, byte part, byte list, ushort slot, bool present)
    {
        That(body.Length == 6 && body[0] == 1 && body[1] == (present ? 1 : 0) &&
            body[2] == part && body[3] == list &&
            BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4)) == slot,
            "Buff强化差量收敛请求部位的存在状态与当前位置");
    }

    static UserInfoSection ReadUserInfoSection(byte[] player)
    {
        int statsLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(player.AsSpan(32)));
        int at = checked(36 + statsLength);
        at++; // equipment_slot_flags
        int equipmentCount = player[at++];
        That(equipmentCount == 0,
            "Buff强化TCP夹具不穿戴装备，USERINFO登记段可独立定位");
        at += 5; // 穿戴段后的u32与两个标量字段。
        ushort skill = BinaryPrimitives.ReadUInt16LittleEndian(player.AsSpan(at));
        at += 2;
        int count = player[at++];
        var items = new UserInfoItem[count];
        for (int index = 0; index < items.Length; index++)
        {
            That(player[at++] == 0, "USERINFO登记项未知前缀保持零");
            byte list = player[at++];
            ushort slot = BinaryPrimitives.ReadUInt16LittleEndian(player.AsSpan(at));
            at += 2;
            ushort rawSlot = BinaryPrimitives.ReadUInt16LittleEndian(player.AsSpan(at));
            int itemId = BinaryPrimitives.ReadInt32LittleEndian(player.AsSpan(at + 2));
            That(rawSlot == slot, "USERINFO登记项外层与123字节核心位置一致");
            at += 123;
            items[index] = new(list, slot, itemId);
        }
        return new(skill, items);
    }

    readonly record struct UserInfoSection(ushort SkillId, UserInfoItem[] Items);
    readonly record struct UserInfoItem(byte List, ushort Slot, int ItemId);
}
