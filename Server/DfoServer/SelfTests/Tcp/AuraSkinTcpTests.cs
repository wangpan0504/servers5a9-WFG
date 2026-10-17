using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class AuraSkinTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        const int ticket = 490700411, commodity = 103818, firstAura = 401590036, secondAura = 401590037;
        var items = resources.ItemResources.Items;
        var product = resources.ItemResources.CashShop.Products[commodity];
        var definition = items.Get(ticket);
        That(product.ItemId == ticket && product.CeraPrice == 3000 &&
            definition is { Kind: ItemKind.Material, StackableType: "[material] 0" } &&
            items.Get(firstAura) is { Kind: ItemKind.Avatar, EquipmentSlot: 9 } &&
            items.Get(secondAura) is { Kind: ItemKind.Avatar, EquipmentSlot: 9 },
            "真实商城商品、材料券与两件光环资源保持已证合同");
        byte job = Enumerable.Range(0, 16).Select(value => (byte)value).First(value =>
            (items.Get(firstAura).Jobs & (1 << value)) != 0 && (items.Get(secondAura).Jobs & (1 << value)) != 0);
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "aura-skin"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var role = repository.Create(owner, new("AuraTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            { Job = job, Level = 95, AcceptedStoryLevel = 1, EquipmentSlotFlags = 17,
                Skills = resources.Characters.Get(job).Skills });
            role = InventoryService.Commit(repository, owner, role,
                new CurrencyService(resources.ItemResources.GoldLimits).Grant(role, CurrencyKind.Cera, 3000));
            var inventory = new InventoryService(items, resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces);
            InventoryService.Commit(repository, owner, role, inventory.Grant(role,
                [new(firstAura, 1), new(secondAura, 1)]));
            repository.Create(owner, new("AuraOther", resources.Towns.Room, resources.Towns.InitialReturn)
            { Job = 15, Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(15).Skills });
        }
        await fixture.StartAsync();
        short sourceSlot;
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            string before = State();
            peer.Send(865, [255, 255, 11, 0]); peer.Expect(1, 865, [0, 1, 0, 11, 0]);
            peer.Send(865, [255, 255, 33, 0]); peer.Expect(1, 865, [0, 1, 0, 33, 0]);
            peer.Barrier();
            That(State() == before, "两类缺券引导保持角色与资产不变");
            Move(peer, 0, firstAura, 11, 0, accepted: false);
            That(State() == before, "未开通时不能穿入外观光环");
            var buy = new byte[19]; buy[2] = 1; buy[5] = 255;
            BinaryPrimitives.WriteInt32LittleEndian(buy.AsSpan(7), commodity);
            peer.Send(64, buy);
            That(peer.Expect(1, 64).Body[0] == 1, "真实商城商品购买成功");
            byte[] delta = peer.Expect(0, 14).Body;
            That(delta[0] == 0 && U32(delta, 5) == ticket && U32(delta, 9) == 1,
                "购买获得真实材料券，不提前开通资格");
            sourceSlot = checked((short)U16(delta, 3));
            That(sourceSlot >= 121 && U32(peer.Expect(0, 53).Body, 1) == 0,
                "材料券进入真实材料页，商城只扣实际点券");
            peer.Barrier();
            before = State();
            if (!definition.OpensAuraSkinSlot)
            {
                peer.Send(515, ActionBody(sourceSlot));
                peer.Expect(1, 515, [0, 0, 0, (byte)sourceSlot, (byte)(sourceSlot >> 8), 0, 127]);
                peer.Barrier();
                That(State() == before, "原PVF未声明127动作时伪造515不能消费材料券或开栏");
            }
            fixture.Sql("CREATE TRIGGER fail_aura_tcp BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='open_aura_skin_slot' BEGIN SELECT RAISE(ABORT,'aura rollback'); END");
            peer.Send(865, OpenBody(sourceSlot)); peer.Closed();
            That(State() == before, "后段SQL失败原子回滚券、bit3、revision及审计");
            fixture.Sql("DROP TRIGGER fail_aura_tcp");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            peer.Send(865, OpenBody(sourceSlot));
            byte[] removed = peer.Expect(0, 14).Body;
            That(removed[0] == 0 && U16(removed, 3) == sourceSlot && U32(removed, 5) == uint.MaxValue,
                "成功先同步真实券源清空");
            byte[] player = peer.Expect(0, 2).Body;
            That(player[0] == 1 && player[21] == 0 && player[36 + U32(player, 32)] == 25,
                "Player保留宠物独立头与其它flags，光环bit3立即生效");
            peer.Expect(1, 865, [1, 11, 0]); peer.Barrier();
            string before = State();
            peer.Send(865, OpenBody(sourceSlot)); peer.Expect(1, 865, [0, 0, 0, 11, 0]);
            peer.Send(865, [255, 255, 11, 0]); peer.Expect(1, 865, [0, 0, 0, 11, 0]);
            peer.Barrier();
            That(State() == before, "重复865和已开通缺券不再次扣券或误引导");
            Move(peer, 0, firstAura, 9, 0, accepted: true);
            Move(peer, 1, secondAura, 11, 0, accepted: true);
            string worn = AuraAssets();
            Move(peer, 1, secondAura, 11, 0, accepted: false);
            That(AuraAssets() == worn, "旧源重放不复制或反向移动光环实例");
            Move(peer, 1, 0, 11, secondAura, accepted: true);
            Move(peer, 1, secondAura, 11, 0, accepted: true);
            Move(peer, 0, 0, 9, firstAura, accepted: true);
            That(fixture.Query("SELECT count(*) FROM character_inventory_slots WHERE character_id=$p0 AND list_type=3 AND slot_index=11", peer.Id)
                is [[1L]], "卸下真光环后仍保留外观资产，不转移真实9");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("restart");
        using (var peer = Open(fixture))
        {
            var selected = peer.Select();
            That(selected.Single(packet => packet.Kind == 0 && packet.Opcode == 2 && packet.Body[0] == 1).Body[36 +
                U32(selected.Single(packet => packet.Kind == 0 && packet.Opcode == 2 && packet.Body[0] == 1).Body, 32)] == 25,
                "进程重启后Player恢复角色光环开通位");
            peer.Barrier();
        }
        That(fixture.Query("SELECT equipment_slot_flags FROM characters ORDER BY slot") is [[25L], [0L]] &&
            fixture.Query("SELECT count(*) FROM inventory_audit_log WHERE action_name='open_aura_skin_slot'") is [[1L]] &&
            fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "光环开通按角色隔离，只有一次消费审计且数据库完整");

        string State() => fixture.Snapshot("SELECT id,revision,equipment_slot_flags FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY account,currency_kind");
        string AuraAssets() => fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items WHERE json_extract(item_core,'$.Kind')=8 ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_avatar_details ORDER BY item_uid");
    }

    static byte[] OpenBody(short source) => Build(writer => { writer.Write(source); writer.Write((ushort)11); });
    static byte[] ActionBody(short source) => Build(writer =>
    {
        writer.Write((ushort)source); writer.Write((byte)0); writer.Write(0u); writer.Write(127u);
        writer.Write(0u); writer.Write(0u);
    });
    static void Move(TcpPeer peer, short bag, int fromId, short worn, int toId, bool accepted)
    {
        peer.Send(19, Build(writer =>
        {
            writer.Write((byte)1); writer.Write(bag); writer.Write(fromId); writer.Write(1);
            writer.Write((byte)3); writer.Write(worn); writer.Write(toId); writer.Write(0); writer.Write(-1); writer.Write((ushort)0);
        }));
        if (!accepted) { peer.Expect(1, 19, [0, 23, 0, 1, 3]); peer.Barrier(); return; }
        peer.Expect(1, 19, Build(writer =>
        { writer.Write((byte)1); writer.Write((byte)1); writer.Write(bag); writer.Write(1); writer.Write((byte)3); writer.Write(worn); }));
        That(peer.Expect(0, 14).Body[0] == 1, "装扮页差量沿原完整实例投影");
        That(peer.Expect(0, 2).Body[0] == 0 && peer.Expect(0, 2).Body[0] == 1,
            "城镇光环装卸按Actor再Player刷新外观和真实槽");
        peer.Barrier();
    }
}
