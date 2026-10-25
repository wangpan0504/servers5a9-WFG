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

    static byte[] MoveItem(short source, int itemId, short destination, byte sourceList = 0, byte targetList = 0) => Build(writer =>
    {
        writer.Write(sourceList);
        writer.Write(source);
        writer.Write(itemId);
        writer.Write(1);
        writer.Write(targetList);
        writer.Write(destination);
        writer.Write(0);
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
