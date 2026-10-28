using System.Text.Json;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class GiftCompatibilityTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "gift-compatibility"), gm: true, resources: resources);
        var items = resources.ItemResources.Items;
        int[] boxes = [2683229, 490703983, 490704027, 490704044, 490704051];
        var repo = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repo.Create(owner, new("GiftCompat", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = 0, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new(boxes.Select((id, index) => new InventoryItem(0, new(InventoryListType.Main, (short)(65 + index)),
                    InventoryRewardGrantService.Create(items.Get(id), shopExpiration: true))).ToImmutableArray())
            });
        var row = resources.ItemResources.AvatarPackages.Get(boxes[0]).Categories.Single().Rows[0];
        var avatar = resources.ItemResources.AvatarPackages.GetAvatar(row.ItemId);
        byte[] Selection() => Build(writer =>
        {
            writer.Write((short)65); writer.Write(1); writer.Write((byte)0); writer.Write((byte)0); writer.Write(row.ItemId);
            writer.Write((byte)(avatar.RequiresAbilityChoice ? 1 : 0));
            if (avatar.RequiresAbilityChoice) { writer.Write(row.ItemId); writer.Write(row.ForcedAbility ?? checked((byte)row.DefaultAbility)); }
            writer.Write((byte)0);
        });
        byte[] OpenPet(short slot) => Build(writer => { writer.Write(slot); writer.Write(1); });
        await fixture.StartAsync();
        BoosterTcpTests.SetExpirationDebug(fixture, true);
        foreach (var failure in new[] { (Table: "inventory_avatar_details", Body: Selection()),
            (Table: "inventory_creature_details", Body: OpenPet(66)) })
        {
            fixture.Sql($"CREATE TRIGGER fail_gift_detail BEFORE INSERT ON {failure.Table} BEGIN SELECT RAISE(ABORT,'gift detail rollback'); END");
            string before;
            using (var peer = Open(fixture))
            {
                peer.Select(); peer.Barrier(); before = State();
                peer.Send(160, failure.Body); peer.Closed();
            }
            That(State() == before, "无分类装扮及随机宠物详情写入失败时源、UID、版本及审计整笔回滚");
            fixture.Sql("DROP TRIGGER fail_gift_detail");
        }
        byte[] committedPets = [];
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            string before = State();
            byte[] invalid = Selection(); invalid[6] = 1;
            peer.Send(160, invalid); peer.Expect(1, 160, [0, 19, 0]); peer.Barrier();
            That(before == State(), "无分类礼盒非法坐标失败ACK收尾，连接与全部资产保持");
            peer.Send(160, Selection());
            BoosterTcpTests.ExpectAck(peer, boxes[0], 65, [(row.ItemId, row.Count)]);
            BoosterTcpTests.Entry(peer.Expect(0, 14).Body, 65, -1, 0);
            var changed = peer.Expect(0, 14).Body;
            That(changed[0] == 1 && U16(changed, 1) == row.Count && U32(changed, 5) == row.ItemId,
                "无分类真实礼盒在提交后发送源差量与完整装扮专属页");
            int[] pets = [400990271, 400990276, 400990275, 400990284];
            for (int index = 0; index < pets.Length; index++)
            {
                short slot = (short)(66 + index);
                peer.Send(160, OpenPet(slot));
                BoosterTcpTests.ExpectAck(peer, boxes[index + 1], slot, [(pets[index], 1)]);
                committedPets = peer.Expect(0, 105).Body;
                That(committedPets[0] == index + 1, "随机宠物奖励先同步全部已提交详情");
                BoosterTcpTests.Entry(peer.Expect(0, 14).Body, slot, -1, 0);
                changed = peer.Expect(0, 14).Body;
                That(changed[0] == 7 && U16(changed, 1) == 1 && changed.Length == 126 && U32(changed, 5) == pets[index] && U32(changed, 9) > 0,
                    "四种真实随机宠物礼盒均同步完整123B核心与已提交UID");
                peer.Barrier(); before = State();
                peer.Send(160, OpenPet(slot)); peer.Expect(1, 160, [0, 19, 0]); peer.Barrier();
                That(before == State(), "随机宠物礼盒耗尽重放不重复奖励或变更版本");
            }
            peer.Barrier(); before = State();
            peer.Send(160, Selection()); peer.Expect(1, 160, [0, 19, 0]); peer.Barrier();
            That(before == State(), "无分类礼盒耗尽重放不重复装扮");
        }
        await fixture.StopAsync();
        string committed = State();
        await fixture.StartAsync("reopened");
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            That(peer.SpecialContainers.Single(packet => packet.Opcode == 105).Body.SequenceEqual(committedPets),
                "服务重建及重连后恢复四种随机宠物的完整详情");
        }
        await fixture.StopAsync();
        That(State() == committed && fixture.Query("SELECT count(*) FROM inventory_avatar_details") is [[1L]] &&
            fixture.Query("SELECT count(*) FROM inventory_creature_details") is [[4L]] &&
            fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "无分类装扮和随机宠物保存、重建及完整性验证，不能丢奖励或重复奖励");
        string State() => JsonSerializer.Serialize(new[] {
            fixture.Query("SELECT revision FROM characters"),
            fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid"),
            fixture.Query("SELECT list_type,slot_index,item_uid FROM character_inventory_slots ORDER BY list_type,slot_index"),
            fixture.Query("SELECT item_uid,detail FROM inventory_avatar_details ORDER BY item_uid"),
            fixture.Query("SELECT item_uid,detail FROM inventory_creature_details ORDER BY item_uid"),
            fixture.Query("SELECT count(*) FROM inventory_audit_log"),
            fixture.Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'") });
    }
}
