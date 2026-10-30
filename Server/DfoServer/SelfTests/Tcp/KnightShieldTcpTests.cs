using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Persistence;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class KnightShieldTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        KnightShieldTests.RunResources(resources.Archive, resources.ItemResources.Items);
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "knight-shield"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            repository.Create(owner, new("Shield", resources.Towns.Room, resources.Towns.InitialReturn)
                { Job = 12, GrowType = 1, Level = 35, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(12).Skills });
            repository.Create(owner, new("Other", resources.Towns.Room, resources.Towns.InitialReturn)
                { Job = 11, Level = 35, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(11).Skills });
            foreach (bool customized in new[] { true, false })
            {
                var oldRole = repository.Create(owner, new(customized ? "OldCustom" : "OldPlain", resources.Towns.Room, resources.Towns.InitialReturn)
                    { Job = 12, GrowType = 1, Level = 35, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(12).Skills });
                var core = InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(113370008));
                if (customized) core = core with { EmblemSocketCount = 2, Emblem1 = 3321001 };
                repository.CommitInventory(owner, oldRole, new(oldRole.Inventory, oldRole.Inventory with
                    { Items = [new(0, InventoryKnightShieldService.MainSlot, core)] }, "shield_legacy_fixture"));
            }
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            Bytes(peer.KnightShieldDeckBody, Deck(), "守护者本人Player建立后580初始化空五项");
            string initial = Snapshot();
            foreach (byte[] bad in new[] { new byte[19], new byte[21], Deck(113370008), Deck(0, 113370008, 113370008),
                Deck(0, 113370040), Deck(0, 113370003), Deck(0, -1) })
            {
                peer.Send(657, bad);
                peer.Expect(1, 657, [0, 23, 0]);
                peer.Expect(0, 580, Deck());
            }
            peer.Barrier();
            That(Snapshot() == initial, "畸形、越等级、未完成任务及重复配置不写库且连接保持");
            peer.Send(657, Deck(0, 113370029, 113370008));
            peer.Expect(1, 657, [1, 0, .. Deck(0, 113370029, 113370008)]);
            peer.Barrier();
            CheckDeck(0, 113370029, 113370008);
            peer.Send(19, Move(33, 2, 0, 33, 0));
            peer.Expect(1, 19, [1, 33, 2, 0, 0, 0, 0, 0, 33, 0, 0]);
            Equipment(peer, clear: false);
            peer.Expect(0, 580, Deck(113370008, 113370029));
            peer.Barrier();
            CheckDeck(113370008, 113370029);
            That(fixture.Query("SELECT json_extract(i.item_core,'$.ItemId') FROM character_inventory_slots s " +
                "JOIN character_inventory_items i USING(item_uid) WHERE s.character_id=1 AND s.list_type=3 AND s.slot_index=24") is [[113370008L]],
                "主盾移入后真实24模板与配置同一次提交");
            string equipped = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_shield BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='knight_shield_deck' BEGIN SELECT RAISE(ABORT,'shield rollback'); END");
            peer.Send(19, Move(33, 0, 0, 34, 5));
            peer.Expect(1, 19, [0, 23, 0, 33, 34]);
            peer.Expect(0, 580, Deck(113370008, 113370029));
            peer.Barrier();
            That(Snapshot() == equipped, "后段SQL拒绝恢复乐观配置，实体和revision均完整回滚");
            fixture.Sql("DROP TRIGGER fail_shield");
            peer.Send(19, Move(33, 0, 0, 33, 1));
            peer.Expect(1, 19, [1, 33, 0, 0, 0, 0, 0, 0, 33, 1, 0]);
            Equipment(peer, clear: true);
            peer.Expect(0, 580, Deck(113370029, 113370008));
            peer.Barrier();
            CheckDeck(113370029, 113370008);
            peer.Send(19, Move(3, 24, 113370029, 0, 0, count: 1));
            peer.Expect(1, 19, [0, 23, 0, 3, 0]);
            peer.Barrier();
            CheckDeck(113370029, 113370008);
            string protectedMain = Snapshot();
            peer.Send(18, DeleteMain());
            peer.Expect(1, 18, Convert.FromHexString("000400000008031801"));
            peer.Barrier();
            That(Snapshot() == protectedMain, "真实删除入口不能删掉已配置主盾而留下失配配置");
        }
        using (var peer = Open(fixture))
        {
            string original = LegacyItem(3);
            peer.Select(2, knightShieldDeck: Five([113370008]));
            That((string)fixture.Query("SELECT knight_shield_deck FROM characters WHERE id=3")[0][0]! == "[0,0,0,0,0]",
                "登录只读推导旧盾配置，不写角色数据");
            for (int repeat = 0; repeat < 2; repeat++)
            {
                peer.Send(19, Move(34, 0, 113370008, 33, 0));
                peer.Expect(1, 19, [1, 34, 0, 0, 0, 0, 0, 0, 33, 0, 0]);
                byte[] expectedCore = new byte[126];
                expectedCore[0] = 3; expectedCore[1] = 1; expectedCore[3] = 24;
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(expectedCore.AsSpan(5), 113370008);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(expectedCore.AsSpan(9), 999_999_998);
                expectedCore[63] = 2;
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(expectedCore.AsSpan(64), 3321001);
                peer.Expect(0, 14, expectedCore);
                That(peer.Expect(0, 2).Body[0] == 0 && peer.Expect(0, 2).Body[0] == 1,
                    "首次与重复同主位19都在ACK之后恢复真实123B核心及角色属性");
                peer.Expect(0, 580, Deck(113370008));
            }
            peer.Send(657, Deck(113370008, 113370029));
            peer.Expect(1, 657, [1, 0, .. Deck(113370008, 113370029)]);
            peer.Barrier();
            That(LegacyItem(3) == original, "旧定制盾首次19与657只保存配置，原UID和完整有孔核心不变");
            string protectedCustom = Snapshot();
            foreach (byte[] move in new[] { Move(33, 1, 0, 33, 0), Move(33, 0, 0, 34, 5) })
            {
                peer.Send(19, move);
                peer.Expect(1, 19, [0, 23, 0, 33, move[11]]);
                peer.Expect(0, 580, Deck(113370008, 113370029));
            }
            peer.Barrier();
            That(Snapshot() == protectedCustom, "定制盾切换和移除均拒绝，完整资产不变");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(3, knightShieldDeck: Five([113370008]));
            peer.Send(19, Move(34, 0, 113370029, 33, 0));
            peer.Expect(1, 19, [1, 34, 0, 0, 0, 0, 0, 0, 33, 0, 0]);
            Equipment(peer, clear: true);
            peer.Expect(0, 580, Deck(113370029));
            peer.Barrier();
            That((string)fixture.Query("SELECT knight_shield_deck FROM characters WHERE id=4")[0][0]! == "[113370029,0,0,0,0]",
                "默认旧盾首次合法换盾直接成功，不要求故意失败初始化");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("restart");
        using (var peer = Open(fixture))
        {
            peer.Select();
            Bytes(peer.KnightShieldDeckBody, Deck(113370029, 113370008), "重启重连保留五项顺序及主盾");
            peer.Send(19, Move(33, 0, 0, 34, 5));
            peer.Expect(1, 19, [1, 33, 0, 0, 0, 0, 0, 0, 34, 5, 0]);
            Equipment(peer, clear: false);
            peer.Expect(0, 580, Deck(0, 113370008));
            peer.Barrier();
            CheckDeck(0, 113370008);
        }
        using (var peer = Open(fixture))
        {
            peer.Select(1);
            Bytes(peer.KnightShieldDeckBody, [], "其他职业不下发盾牌配置");
            peer.Send(657, Deck(0, 113370008));
            peer.Expect(1, 657, [0, 23, 0]);
            peer.Expect(0, 580, Deck());
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.Select(2);
            Bytes(peer.KnightShieldDeckBody, Deck(113370008, 113370029), "重启后旧定制盾配置已持久化且完整核心保留");
            peer.Barrier();
        }

        void CheckDeck(params int[] ids) => That((string)fixture.Query("SELECT knight_shield_deck FROM characters WHERE id=1")[0][0]! ==
            System.Text.Json.JsonSerializer.Serialize(Five(ids)), "数据库保留精确五项及空洞");
        string Snapshot() => fixture.Snapshot("SELECT id,revision,knight_shield_deck FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
        string LegacyItem(int id) => fixture.Snapshot("SELECT i.* FROM character_inventory_items i JOIN character_inventory_slots s USING(item_uid) WHERE s.character_id=" + id);
    }

    static void Equipment(TcpPeer peer, bool clear)
    {
        if (clear) Delta(peer);
        Delta(peer);
        That(peer.Expect(0, 2).Body[0] == 0 && peer.Expect(0, 2).Body[0] == 1,
            "完整核心差量之后恢复Actor与本人属性");
    }

    static void Delta(TcpPeer peer)
    {
        var body = peer.Expect(0, 14).Body;
        That(body.Length >= 7 && body[0] == 3 && U16(body, 1) == 1 && U16(body, 3) == 24,
            "盾牌只更新真实装备24，不把33/34当普通背包");
    }

    static int[] Five(int[] ids) => [.. ids, .. Enumerable.Repeat(0, 5 - ids.Length)];
    static byte[] Deck(params int[] ids) => Build(writer => { foreach (int id in Five(ids)) writer.Write(id); });
    static byte[] DeleteMain() => Build(writer =>
    {
        byte[] child = Build(w => { w.Write(new byte[] { 8, 0, 16, 24, 24 }); w.Write7BitEncodedInt(InventoryRewardGrantService.InitialQuality); w.Write(new byte[] { 32, 1 }); });
        byte[] payload = Build(w => { w.Write(new byte[] { 8, 18, 16, 3, 26, (byte)child.Length }); w.Write(child); w.Write(new byte[] { 32, 0 }); });
        writer.Write(payload.Length); writer.Write(payload);
    });
    static byte[] Move(byte source, ushort from, int id, byte target, ushort to, int count = 0) => Build(writer =>
    {
        writer.Write(source); writer.Write(from); writer.Write(id); writer.Write(count);
        writer.Write(target); writer.Write(to); writer.Write(0); writer.Write(0); writer.Write(-1);
        writer.Write((byte)0); writer.Write((byte)0);
    });
}
