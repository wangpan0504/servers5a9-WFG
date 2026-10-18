using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using PvfLib;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class EquipmentEmblemTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var targetTemplate = resources.Characters.Get(0).Equipment
            .Select(row => row.Template).First(template => template.EquipmentSlot == 14);
        var emblemTemplate = SelectGreenEmblem(resources);
        const short targetSlot = 9;
        short materialSlot = ItemSlotBoundService.Range(ItemKind.Emblem, InventorySnapshot.Empty).Start;
        var targetCore = InventoryRewardGrantService.Create(targetTemplate) with
        {
            Attr = 7,
            Durability = 44,
            Marker16 = 123456,
            Rune = 8,
            Random0 = new(1, 2, 3)
        };
        var target = InventoryRewardGrantService.CreateItem(targetTemplate,
            new(InventoryListType.Main, targetSlot), targetCore);
        var material = InventoryRewardGrantService.CreateItem(emblemTemplate,
            new(InventoryListType.Main, materialSlot), InventoryRewardGrantService.Create(emblemTemplate, 3));
        byte[] openBody = Build(writer =>
        {
            writer.Write(targetSlot);
            writer.Write(targetTemplate.Id);
            writer.Write(materialSlot);
        });
        byte[] useBody = Build(writer =>
        {
            writer.Write(targetSlot);
            writer.Write(targetTemplate.Id);
            writer.Write((byte)2);
            writer.Write(materialSlot);
            writer.Write(emblemTemplate.Id);
            writer.Write((byte)0);
            writer.Write(materialSlot);
            writer.Write(emblemTemplate.Id);
            writer.Write((byte)1);
        });

        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "equipment-emblem"), resources: resources);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            new CharacterRepository(fixture.Database).Create(owner,
                new("EmblemTcp", resources.Towns.Room, resources.Towns.InitialReturn)
                {
                    Level = 95,
                    AcceptedStoryLevel = 1,
                    Skills = resources.Characters.Get(0).Skills,
                    Inventory = new([target, material])
                });
        string original = Snapshot();
        string originalTargetUid = TargetUid();
        await fixture.StartAsync();

        using (var peer = Open(fixture))
        {
            peer.Send(916, openBody);
            peer.Expect(1, 916, [0, 4, 0]);
            That(original == Snapshot(), "未选角开孔拒绝，不消费材料或增加revision");
            peer.Select();
            peer.Barrier();
            string before = Snapshot();
            peer.Send(916, openBody[..^1]);
            peer.Expect(1, 916, [0, 4, 0]);
            peer.Barrier();
            That(before == Snapshot(), "C916非8字节正文拒绝且连接可继续");

            fixture.Sql("CREATE TRIGGER fail_equipment_socket_late BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='equipment_socket_open' BEGIN SELECT RAISE(ABORT,'socket rollback'); END");
            peer.Send(916, openBody);
            peer.Closed();
            That(before == Snapshot(), "开孔末段审计失败回滚目标、材料、revision及审计，不提前发送成功");
            fixture.Sql("DROP TRIGGER fail_equipment_socket_late");
        }

        using (var peer = Open(fixture))
        {
            var entry = peer.Select();
            var closedRow = Row(entry.First(packet =>
                packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 0).Body, targetSlot, full: true);
            That(closedRow[60] == 0 && I32(closedRow, 61) == 0 && I32(closedRow, 65) == 0,
                "失败后重连仍恢复未开孔完整装备");
            peer.Barrier();

            peer.Send(916, openBody);
            var openedDelta = peer.Expect(0, 14).Body;
            var openedRow = Row(openedDelta, targetSlot);
            That(openedRow[60] == 2 && I32(openedRow, 61) == -1 && I32(openedRow, 65) == -1 &&
                openedRow[10] == targetCore.Attr && U16(openedRow, 11) == targetCore.Durability,
                "C916先投影目标完整核心并把上衣两个孔初始化为-1");
            peer.Expect(1, 916, [1, (byte)targetSlot, 0, (byte)materialSlot, (byte)(materialSlot >> 8)]);
            peer.Barrier();
            That(MaterialCount() == 2 && SocketState() is [[2L, -1L, -1L]],
                "C916 ACK处理材料槽，数据库同次提交保存开放孔位和剩余数量");
            string opened = Snapshot();
            peer.Send(916, openBody);
            peer.Expect(1, 916, [0, 4, 0]);
            peer.Barrier();
            That(opened == Snapshot(), "已开孔目标拒绝重复消耗开孔材料");

            fixture.Sql("CREATE TRIGGER fail_equipment_emblem_late BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='equipment_emblem_use' BEGIN SELECT RAISE(ABORT,'emblem rollback'); END");
            peer.Send(915, useBody);
            peer.Closed();
            That(opened == Snapshot(), "镶嵌末段审计失败回滚双孔、同栈材料和revision");
            fixture.Sql("DROP TRIGGER fail_equipment_emblem_late");
        }

        using (var peer = Open(fixture))
        {
            var entry = peer.Select();
            var openedRow = Row(entry.First(packet =>
                packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 0).Body, targetSlot, full: true);
            That(openedRow[60] == 2 && I32(openedRow, 61) == -1 && I32(openedRow, 65) == -1,
                "镶嵌失败后重连保留两个开放空孔");
            peer.Barrier();

            peer.Send(915, useBody);
            var usedDelta = peer.Expect(0, 14).Body;
            var usedRow = Row(usedDelta, targetSlot);
            That(usedRow[60] == 2 && I32(usedRow, 61) == emblemTemplate.Id &&
                I32(usedRow, 65) == emblemTemplate.Id,
                "C915先投影已提交目标，同一徽章栈覆盖两个不同孔位");
            peer.Expect(1, 915,
                [1, 1, (byte)materialSlot, (byte)(materialSlot >> 8), 0, 0, 0, 0]);
            peer.Barrier();
            That(MaterialCount() == 0 &&
                SocketState() is [[2L, long first, long second]] &&
                first == emblemTemplate.Id && second == emblemTemplate.Id,
                "C915 ACK聚合为一个材料槽的绝对剩余0，持久化双孔徽章");
            string committed = Snapshot();
            peer.Send(915, useBody);
            peer.Expect(1, 915, [0, 4, 0]);
            peer.Barrier();
            That(committed == Snapshot(), "材料耗尽后重放C915不重复覆盖或增加revision");
        }

        using (var peer = Open(fixture))
        {
            var entry = peer.Select();
            var restored = Row(entry.First(packet =>
                packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 0).Body, targetSlot, full: true);
            That(restored[60] == 2 && I32(restored, 61) == emblemTemplate.Id &&
                I32(restored, 65) == emblemTemplate.Id,
                "再次重连由完整Main条目恢复普通装备双孔徽章");
            peer.Barrier();
        }
        await fixture.StopAsync();

        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var saved = new CharacterRepository(fixture.Database).List(owner).Single();
            That(saved.Inventory.Find(new(InventoryListType.Main, materialSlot)) is null &&
                saved.Inventory.Find(new(InventoryListType.Main, targetSlot))!.Core ==
                targetCore with
                {
                    EmblemSocketCount = 2,
                    Emblem1 = emblemTemplate.Id,
                    Emblem2 = emblemTemplate.Id
                },
                "仓储重开保留目标UID外的全部核心字段，材料同栈恰好消费三件");
        }
        That(TargetUid() == originalTargetUid && fixture.Query("PRAGMA integrity_check") is [["ok"]] &&
            fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "开孔和镶嵌不重建目标实例且无孤立资产或外键损坏");

        int MaterialCount() => fixture.Query("""
            SELECT json_extract(i.item_core,'$.Value') FROM character_inventory_items i
            JOIN character_inventory_slots s USING(item_uid)
            WHERE s.list_type=0 AND s.slot_index=$p0
            """, materialSlot) is [[long count]] ? checked((int)count) : 0;
        object[][] SocketState() => fixture.Query("""
            SELECT json_extract(i.item_core,'$.EmblemSocketCount'),
                   json_extract(i.item_core,'$.Emblem1'),
                   json_extract(i.item_core,'$.Emblem2')
            FROM character_inventory_items i
            JOIN character_inventory_slots s USING(item_uid)
            WHERE s.list_type=0 AND s.slot_index=$p0
            """, targetSlot);
        string TargetUid() => fixture.Snapshot("""
            SELECT i.item_uid FROM character_inventory_items i
            JOIN character_inventory_slots s USING(item_uid)
            WHERE s.list_type=0 AND s.slot_index=$p0
            """, targetSlot);
        string Snapshot() => fixture.Snapshot("SELECT id,revision,gold FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM sqlite_sequence ORDER BY name");
    }

    static ItemTemplate SelectGreenEmblem(TcpTestResources resources)
    {
        var stackable = PvfScript.List(resources.Archive.ReadText("stackable/stackable.lst"));
        foreach (int itemId in stackable.Where(row =>
                     row.Value.Contains("emblem", StringComparison.OrdinalIgnoreCase))
                 .Select(row => row.Key))
        {
            try
            {
                var template = resources.ItemResources.Items.Get(itemId);
                if (template.Kind == ItemKind.Emblem && (template.EmblemTargetSockets & 4) != 0)
                    return template;
            }
            catch (InvalidDataException) { }
        }
        throw new InvalidDataException("现场stackable名册没有可用于绿色普通装备孔的徽章");
    }

    static byte[] Row(byte[] body, short slot, bool full = false)
    {
        int header = full ? 5 : 3;
        int count = U16(body, full ? 3 : 1);
        That(body.Length == header + count * 123, "普通装备镶嵌条目保持完整123B合同");
        int offset = Enumerable.Range(0, count).Select(index => header + index * 123)
            .Single(at => U16(body, at) == slot);
        return body.AsSpan(offset, 123).ToArray();
    }

    static int I32(byte[] value, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(offset));

    static ushort U16(byte[] value, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(offset));
}
