using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class CreatureSkinTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        const int ticket = 490703887, commodity = 105301;
        var product = resources.ItemResources.CashShop.Products[commodity];
        That(product.ItemId == ticket && product.CeraPrice == 3000 && resources.ItemResources.Items.Get(ticket).OpensCreatureSkinSlot,
            "实际商城商品与已证开栏动作资源对应");
        await using (var rightClick = new TcpTestFixture(options, Path.Combine(root, "creature-skin-right-click"), resources: resources))
        {
            var rightClickRepository = new CharacterRepository(rightClick.Database);
            using (var owner = new AccountOwners().Acquire(rightClick.Options.DevelopmentAccount, () => { }))
            {
                var character = rightClickRepository.Create(owner, new("SkinUse", resources.Towns.Room, resources.Towns.InitialReturn)
                { Level = 60, AcceptedStoryLevel = 1, EquipmentSlotFlags = 17, Skills = resources.Characters.Get(0).Skills });
                var inventory = new InventoryService(resources.ItemResources.Items, resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces);
                InventoryService.Commit(rightClickRepository, owner, character, inventory.Grant(character, [new(ticket, 2)]));
            }
            short rightClickSlot = checked((short)(long)rightClick.Query("SELECT s.slot_index FROM character_inventory_slots s " +
                "JOIN character_inventory_items i USING(item_uid) WHERE json_extract(i.item_core,'$.ItemId')=$p0", ticket)[0][0]!);
            await rightClick.StartAsync();
            using (var peer = Open(rightClick))
            {
                peer.Select();
                string beforeUse = RightClickState();
                foreach (var (offset, value) in new (int Offset, byte Value)[]
                { (2, 1), (3, 1), (11, 1), (15, 1) })
                {
                    var invalid = UseBody(rightClickSlot);
                    invalid[offset] = value;
                    peer.Send(515, invalid);
                    peer.Expect(1, 515, RejectBody((ushort)rightClickSlot, invalid[2]));
                }
                peer.Send(515, UseBody(-1));
                peer.Expect(1, 515, RejectBody(ushort.MaxValue, 0));
                peer.Send(515, UseBody(checked((short)(rightClickSlot + 1))));
                peer.Expect(1, 515, RejectBody((ushort)(rightClickSlot + 1), 0));
                peer.Barrier();
                That(RightClickState() == beforeUse, "右键非法容器、长度范围、保留字段及空源均不扣券不开栏");
                rightClick.Sql("CREATE TRIGGER fail_skin_right_click BEFORE INSERT ON inventory_audit_log " +
                    "WHEN NEW.action_name='open_creature_skin_slot' BEGIN SELECT RAISE(ABORT,'skin rollback'); END");
                peer.Send(515, UseBody(rightClickSlot));
                peer.Expect(1, 515, RejectBody((ushort)rightClickSlot, 0));
                peer.Barrier();
                That(RightClickState() == beforeUse, "右键事务末段SQL失败回滚，并以合法515拒绝保持连接");
                rightClick.Sql("DROP TRIGGER fail_skin_right_click");
                peer.Send(15, new byte[4]);
                peer.Expect(1, 15, [1]);
                peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
                peer.Expect(0, 27, new byte[42]);
                peer.Send(515, UseBody(rightClickSlot));
                peer.Expect(1, 515, RejectBody((ushort)rightClickSlot, 0));
                peer.Barrier();
                That(RightClickState() == beforeUse, "地图选择阶段右键安全拒绝且不扣券");
            }
            using (var peer = Open(rightClick))
            {
                peer.Select();
                peer.Send(515, UseBody(rightClickSlot));
                peer.Expect(1, 515, Build(writer =>
                { writer.Write((byte)1); writer.Write((ushort)rightClickSlot); writer.Write((byte)0); writer.Write(224); }));
                CheckPlayer(peer.Expect(0, 2).Body);
                peer.Barrier();
                That(rightClick.Query("SELECT equipment_slot_flags FROM characters") is [[49L]] &&
                    rightClick.Query("SELECT json_extract(i.item_core,'$.Value') FROM character_inventory_items i") is [[1L]],
                    "右键成功只扣同一堆叠一张并原子保存开通位");
                string afterUse = RightClickState();
                peer.Send(515, UseBody(rightClickSlot));
                peer.Expect(1, 515, RejectBody((ushort)rightClickSlot, 0));
                peer.Barrier();
                That(RightClickState() == afterUse, "重复右键已开通角色不二次扣券");
            }
            await rightClick.StopAsync();
            await rightClick.StartAsync("restart");
            using (var peer = Open(rightClick))
            {
                var selected = peer.Select();
                CheckPlayer(selected.Single(packet => packet.Kind == 0 && packet.Opcode == 2 && packet.Body[0] == 1).Body);
            }
            string RightClickState() => rightClick.Snapshot("SELECT id,revision,equipment_slot_flags FROM characters") +
                rightClick.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
                rightClick.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
        }
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "creature-skin"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repository.Create(owner, new("SkinFirst", resources.Towns.Room, resources.Towns.InitialReturn)
            { Level = 60, AcceptedStoryLevel = 1, EquipmentSlotFlags = 17, Skills = resources.Characters.Get(0).Skills });
            current = InventoryService.Commit(repository, owner, current,
                new CurrencyService(resources.ItemResources.GoldLimits).Grant(current, CurrencyKind.Cera, 3000));
            var inventory = new InventoryService(resources.ItemResources.Items, resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces);
            InventoryService.Commit(repository, owner, current, inventory.Grant(current, [new(63000, 1), new(63023, 1)]));
            repository.Create(owner, new("SkinSecond", resources.Towns.Room, resources.Towns.InitialReturn)
            { Job = 15, Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(15).Skills });
        }
        await fixture.StartAsync();
        short sourceSlot;
        using (var peer = Open(fixture))
        {
            peer.Select();
            string before = Snapshot();
            peer.Send(865, [255, 255, 33, 0]);
            peer.Expect(1, 865, [0, 1, 0, 33, 0]);
            foreach (byte[] invalid in new byte[][] { [], [65, 0], [65, 0, 33, 0, 0], [65, 0, 33, 0] })
            {
                peer.Send(865, invalid);
                peer.Expect(1, 865, [0, 0, 0, 33, 0]);
            }
            peer.Send(865, [255, 255, 11, 0]);
            peer.Expect(1, 865, [0, 1, 0, 11, 0]);
            peer.Barrier();
            That(Snapshot() == before, "宠物与光环缺券引导及畸形请求不写角色或资产，连接继续");
            var buy = new byte[19];
            buy[2] = 1;
            buy[5] = 255;
            BinaryPrimitives.WriteInt32LittleEndian(buy.AsSpan(7), commodity);
            peer.Send(64, buy);
            var purchase = peer.Expect(1, 64).Body;
            That(purchase[0] == 1 && U32(purchase, 6) == commodity, "商城按原商品合同购买开栏券");
            var delta = peer.Expect(0, 14).Body;
            That(delta[0] == 0 && U16(delta, 1) == 1 && U32(delta, 5) == ticket && U32(delta, 9) == 1,
                "购买实时获得一张券，未用券时不提前开通角色");
            sourceSlot = checked((short)U16(delta, 3));
            That(U32(peer.Expect(0, 53).Body, 1) == 0 && fixture.Query("SELECT equipment_slot_flags FROM characters ORDER BY slot")[0] is [17L],
                "商城扣除实际点券，购买不等同使用");
            peer.Barrier();
            before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_skin_tcp BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='open_creature_skin_slot' BEGIN SELECT RAISE(ABORT,'skin rollback'); END");
            peer.Send(865, OpenBody());
            peer.Closed();
            That(Snapshot() == before, "开通位和扣券写入后的SQL失败整笔回滚，无提前成功");
            fixture.Sql("DROP TRIGGER fail_skin_tcp");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Send(865, OpenBody());
            var consumed = peer.Expect(0, 14).Body;
            That(consumed[0] == 0 && U16(consumed, 1) == 1 && U16(consumed, 3) == sourceSlot && U32(consumed, 5) == uint.MaxValue,
                "成功前先发布真实券源清空");
            CheckPlayer(peer.Expect(0, 2).Body);
            peer.Expect(1, 865, [1, 33, 0]);
            peer.Barrier();
            string before = Snapshot();
            foreach (byte[] repeated in new[] { OpenBody(), new byte[] { 255, 255, 33, 0 } })
            {
                peer.Send(865, repeated);
                peer.Expect(1, 865, [0, 0, 0, 33, 0]);
            }
            peer.Barrier();
            That(Snapshot() == before, "重复旧源请求及缺券哨兵均不再扣费、不误引导已开通角色购买");
            string pets = PetAssets();
            MoveCreature(peer, 0, 63000, 26, 0, 63000, 0);
            MoveCreature(peer, 1, 63023, 33, 0, 63000, 63023);
            string equipped = Snapshot();
            peer.Send(19, MoveBody(1, 63023, 33, 0));
            peer.Expect(1, 19, [0, 23, 0, 7, 3]);
            peer.Barrier();
            That(Snapshot() == equipped, "旧源幻化穿入重放不能反向卸装或复制资产");
            MoveCreature(peer, 5, 0, 26, 63000, 0, 0);
            MoveCreature(peer, 5, 63000, 26, 0, 63000, 63023);
            MoveCreature(peer, 1, 0, 33, 63023, 63000, 0);
            MoveCreature(peer, 1, 63023, 33, 0, 63000, 63023);
            That(PetAssets() == pets, "真宠与幻化独立穿脱不更改任一UID、完整核心或成长详情");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("restart");
        using (var peer = Open(fixture))
        {
            var selected = peer.Select();
            CheckPlayer(selected.Single(packet => packet.Kind == 0 && packet.Opcode == 2 && packet.Body[0] == 1).Body);
            CheckActor(selected.Single(packet => packet.Kind == 0 && packet.Opcode == 2 && packet.Body[0] == 0).Body, 63000, 63023);
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            var selected = peer.Select(1);
            That(selected.Single(packet => packet.Kind == 0 && packet.Opcode == 2 && packet.Body[0] == 1).Body[21] == 0,
                "同账号另一职业角色仍未开通幻化栏");
            peer.Send(865, [255, 255, 33, 0]);
            peer.Expect(1, 865, [0, 1, 0, 33, 0]);
            peer.Barrier();
        }
        That(fixture.Query("SELECT equipment_slot_flags FROM characters ORDER BY slot") is [[49L], [0L]] &&
            fixture.Query("SELECT count(*) FROM inventory_audit_log WHERE action_name='open_creature_skin_slot'") is [[1L]] &&
            fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "重启保留角色独立开通与一笔消费审计，完整性及外键正常");

        byte[] OpenBody() => Build(writer => { writer.Write(sourceSlot); writer.Write((ushort)33); });
        static byte[] UseBody(short slot) => Build(writer =>
        { writer.Write((ushort)slot); writer.Write((byte)0); writer.Write(0); writer.Write(224); writer.Write(0); writer.Write(0); });
        static byte[] RejectBody(ushort slot, byte list) => Build(writer =>
        { writer.Write((byte)0); writer.Write((ushort)0); writer.Write(slot); writer.Write(list); writer.Write((byte)224); });
        string PetAssets() => fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items WHERE json_extract(item_core,'$.Kind')=5 ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_creature_details ORDER BY item_uid");
        string Snapshot() => fixture.Snapshot("SELECT id,revision,equipment_slot_flags FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY account,currency_kind");
        static void CheckPlayer(byte[] body)
        {
            int statsLength = checked((int)U32(body, 32));
            That(body[0] == 1 && body[21] == 1 && body[36 + statsLength] == 49,
                "成功及重启Player的17B末字节和独立开通位均来自已提交角色，保留原其它栏位");
        }
    }

    static void MoveCreature(TcpPeer peer, short bag, int fromId, short worn, int toId, uint real, uint skin)
    {
        peer.Send(19, MoveBody(bag, fromId, worn, toId));
        peer.Expect(1, 19, Build(writer =>
        { writer.Write((byte)1); writer.Write((byte)7); writer.Write(bag); writer.Write(1); writer.Write((byte)3); writer.Write(worn); }));
        peer.Expect(0, 105);
        if (worn == 26) peer.Expect(0, 14);
        var delta = peer.Expect(0, 14).Body;
        That(delta[0] == 7, "幻化沿原CMD19处理Equipment33，不重复用通用增量安装；宠物背包保留权威变化");
        CheckActor(peer.Expect(0, 2).Body, real, skin);
        That(peer.Expect(0, 2).Body[0] == 1, "双实体声明之后发布完整穿戴Player");
        peer.Barrier();
    }

    static byte[] MoveBody(short bag, int fromId, short worn, int toId) => Build(writer =>
    {
        writer.Write((byte)7); writer.Write(bag); writer.Write(fromId); writer.Write(1);
        writer.Write((byte)3); writer.Write(worn); writer.Write(toId); writer.Write(0); writer.Write(-1); writer.Write((ushort)0);
    });

    static void CheckActor(byte[] body, uint real, uint skin)
    {
        int nameWidth = checked((int)U32(body, 123));
        int realOffset = 156 + nameWidth + body[133 + nameWidth] * 25;
        That(body[0] == 0 && body[132 + nameWidth] == 0 && U32(body, 94) == skin && U32(body, realOffset) == real,
            "城镇及重登分别恢复真实26声明和固定头独立外观，不以幻化覆盖真宠；卸真宠保留33资产但清外观");
    }
}
