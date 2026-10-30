using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using PvfLib;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class AvatarEmblemTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var items = resources.ItemResources.Items;
        Inventory.AvatarEmblemQualificationResourceTests.Run(items);
        var emblem = PvfScript.List(resources.Archive.ReadText("stackable/stackable.lst"))
            .Where(row => row.Value.Contains("emblem", StringComparison.OrdinalIgnoreCase))
            .Select(row => items.Get(row.Key)).First(item => item.Kind == ItemKind.Emblem && item.EmblemTargetSockets == 1);
        InventoryItem Avatar(int id, InventorySlot position) => InventoryRewardGrantService.CreateItem(items.Get(id), position,
            InventoryRewardGrantService.Create(items.Get(id)) with { Attr = 3, Durability = 3 }) with
            { Avatar = new() { Socket0 = new(239, -1), Socket1 = new(239, -1), Color1 = 1, Color2 = 2 } };
        var skin = Avatar(101580164, new(InventoryListType.Avatar, 0));
        var aura = Avatar(401590018, new(InventoryListType.Avatar, 1));
        var hat = Avatar(101550535, new(InventoryListType.Equipment, 0)) with
            { Avatar = new() { Socket0 = new(1, -1), Socket1 = new(1, -1) } };
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "avatar-emblem"), resources: resources);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            new CharacterRepository(fixture.Database).Create(owner,
                new("EmblemTcp", resources.Towns.Room, resources.Towns.InitialReturn)
                {
                    Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                    Inventory = new([skin, aura, hat, InventoryRewardGrantService.CreateItem(emblem,
                        new(InventoryListType.Main, 289), InventoryRewardGrantService.Create(emblem, 5))], Gold: 4321)
                });
        byte[] pair = Request(skin, emblem.Id, 0, 1);
        string identity = Identity();
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            string beforeSelect = Snapshot();
            peer.Send(201, pair); peer.Expect(1, 201, [0, 1, 0]);
            That(beforeSelect == Snapshot(), "未选角CMD201拒绝且不修改资产");
            peer.Select(); peer.Barrier();
            string before = Snapshot();
            foreach (byte[] bad in new[] { pair[..^1], pair.Concat(new byte[] { 0 }).ToArray(),
                Request(skin, emblem.Id, 0, 0), Request(skin, emblem.Id, 2), Request(skin, emblem.Id + 1, 0) })
            {
                peer.Send(201, bad); peer.Expect(1, 201, [0, 1, 0]); peer.Barrier();
                That(before == Snapshot(), "畸形、重复孔、关闭孔及材料模板不符拒绝，连接保持可用");
            }
            fixture.Sql("CREATE TRIGGER fail_avatar_emblem_late BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='avatar_emblem_use' BEGIN SELECT RAISE(ABORT,'emblem rollback'); END");
            peer.Send(201, pair); peer.Closed();
            That(before == Snapshot(), "末段SQL失败回滚双孔、同栈材料、UID、revision及审计，不提前成功");
            fixture.Sql("DROP TRIGGER fail_avatar_emblem_late");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            Use(peer, skin, pair, emblem.Id, 3, 0, 1);
            Use(peer, aura, Request(aura, emblem.Id, 0, 1), emblem.Id, 1, 0, 1);
            Use(peer, hat, Request(hat, emblem.Id, 0), emblem.Id, 0, 0);
            That(Identity() == identity && MaterialCount() == 0 && SocketCount() == 5,
                "皮肤、光环、已穿戴普通装扮共用入口，五件材料恰好耗尽且不重建目标");
            string committed = Snapshot();
            peer.Send(201, pair); peer.Expect(1, 201, [0, 1, 0]); peer.Barrier();
            That(committed == Snapshot(), "材料耗尽后重发不增加revision、不重复消费或篡改徽章");
        }
        await fixture.StopAsync();
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            byte[] avatars = peer.SpecialContainers.Single(packet => packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 1).Body;
            CheckRow(avatars, 5, skin, emblem.Id, 0, 1);
            CheckRow(avatars, 170, aura, emblem.Id, 0, 1);
            CheckEquippedPlayer(peer.PlayerBody, hat.Core.ItemId, emblem.Id);
            That(MaterialCount() == 0 && SocketCount() == 5 && Identity() == identity,
                "服务重建后背包装扮与穿戴全量恢复五颗徽章，材料及完整身份一致");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "装扮镶嵌重连后无孤立详情或外键损坏");

        int MaterialCount() => fixture.Query("SELECT json_extract(item_core,'$.Value') FROM character_inventory_items " +
            "WHERE json_extract(item_core,'$.ItemId')=$p0", emblem.Id) is [[long count]] ? (int)count : 0;
        int SocketCount() => (int)(long)fixture.Query("SELECT SUM((json_extract(detail,'$.Socket0.EmblemId')=$p0) + " +
            "(json_extract(detail,'$.Socket1.EmblemId')=$p0)) FROM inventory_avatar_details", emblem.Id)[0][0];
        string Identity() => fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items WHERE json_extract(item_core,'$.Kind')=8 ORDER BY item_uid") +
            fixture.Snapshot("SELECT item_uid,json_remove(detail,'$.Socket0','$.Socket1','$.Socket2','$.Socket3','$.Socket4') FROM inventory_avatar_details ORDER BY item_uid");
        string Snapshot() => fixture.Snapshot("SELECT id,revision,gold,avatar_preset_state FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_avatar_details ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") + fixture.Snapshot("SELECT * FROM sqlite_sequence ORDER BY name");
    }

    static void Use(TcpPeer peer, InventoryItem target, byte[] request, int emblemId, byte remaining, params int[] sockets)
    {
        peer.Send(201, request);
        var delta = peer.Expect(0, 14).Body;
        That(delta[0] == (byte)target.Position.List && U16(delta, 1) == 1, "CMD201先发实际目标容器唯一完整差量");
        CheckRow(delta, 3, target, emblemId, sockets);
        if (target.Position.List == InventoryListType.Equipment)
        {
            var preset = peer.Expect(0, 1599).Body;
            That(preset.Length > 4 && preset[0] == 0, "穿戴装扮镶嵌同步当前橱窗记录");
            That(peer.Expect(0, 2).Body[0] == 0, "已穿戴装扮提交后刷新Actor");
            CheckEquippedPlayer(peer.Expect(0, 2).Body, target.Core.ItemId, emblemId);
        }
        peer.Expect(1, 201, [1, 1, 33, 1, remaining, 0, 0, 0]);
        peer.Barrier();
    }

    static void CheckRow(byte[] body, int at, InventoryItem target, int emblem, params int[] sockets)
    {
        That(U16(body, at) == target.Position.Index && U32(body, at + 2) == target.Core.ItemId &&
            body[at + 10] == target.Core.Attr && U16(body, at + 11) == target.Core.Durability &&
            U16(body, at + 161) == target.Avatar!.Color1 && U16(body, at + 163) == target.Avatar.Color2,
            "完整装扮条目保留位置、模板、核心和染色");
        foreach (int socket in sockets)
            That(U32(body, at + 129 + socket * 6) == emblem, "完整五孔详情独立解码提交后的徽章编号");
    }

    static byte[] Request(InventoryItem target, int material, params byte[] sockets) => Build(writer =>
    {
        writer.Write((byte)target.Position.List); writer.Write(target.Position.Index); writer.Write(target.Core.ItemId);
        writer.Write((byte)sockets.Length);
        foreach (byte socket in sockets) { writer.Write((short)289); writer.Write(material); writer.Write(socket); }
    });

    static void CheckEquippedPlayer(byte[] body, int itemId, int emblemId)
    {
        int countAt = checked(37 + (int)U32(body, 32)), at = countAt + 1;
        That(body[0] == 1 && body[countAt] == 1 && body[at] == 0 && U32(body, at + 1) == itemId &&
            U32(body, at + 26) == 30 && U16(body, at + 30) == 1 && U32(body, at + 32) == emblemId,
            "本人Player按统计块长度定位穿戴列表，完整恢复帽子红孔已镶嵌编号");
    }
}
