using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class AvatarPackageTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources, AvatarPackageCatalog packages)
    {
        var items = resources.ItemResources.Items;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "avatar-package"), gm: true, resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repo.Create(owner, new("AvatarBoxes", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = 15, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(15).Skills,
                Inventory = new([.. Enumerable.Range(65, 8).Select(slot => new InventoryItem(0, Bag((short)slot),
                    InventoryRewardGrantService.Create(items.Get(1106), 1000))),
                    new(0, Bag(73), Core(490705385)), new(0, Bag(74), Core(490705425)), new(0, Bag(75), Core(490705426))])
            });
        await fixture.StartAsync();
        BoosterTcpTests.SetExpirationDebug(fixture, true);
        var expected = new List<AvatarPackageChoice>();
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            // 复现真实源前/源后关系：518空选择开启外包385，387作为新实例复用刚耗尽的73格。
            peer.Send(518, [73, 0, 0]); peer.Expect(1, 518, [1, 73, 0]);
            BoosterTcpTests.Entry(peer.Expect(0, 14).Body, 73, -1, 0);
            byte[] outerRewards = peer.Expect(0, 14).Body;
            BoosterTcpTests.Entry(outerRewards, 73, 490705387, 1);
            That(U16(outerRewards, 1) == 14, "固定外包空选择成功，并把全部十四项奖励与新387实例一次提交");
            peer.Barrier();
            BoosterTcpTests.SetExpirationDebug(fixture, false);
            string before = Snapshot(fixture);
            peer.Send(518, [73, 0, 0]); peer.Expect(1, 518, [0, 4, 0]); peer.Barrier();
            That(before == Snapshot(fixture), "合成向490705387发送518空选择时不消费、不自动选择职业、不改变任何资产");
            foreach (var sample in new[] { (Id: 490705387, Slot: (short)73), (Id: 490705425, Slot: (short)74), (Id: 490705426, Slot: (short)75) })
            {
                var definition = packages.Get(sample.Id);
                var category = definition.Categories.Single(row => row.Category == 0 && row.Subcategory == 0);
                var rows = definition.SelectionCount == 0 ? category.Rows.ToArray() : [category.Rows.Single(row => row.ItemId == 42611)];
                var choices = sample.Id == 490705387 ? Inventory.AvatarPackageTests.CapturedChoices() :
                    rows.Select(row => new AvatarPackageChoice(row.ItemId, packages.GetAvatar(row.ItemId).Abilities.Max())).ToArray();
                var body = sample.Id == 490705387 ? Inventory.AvatarPackageTests.CapturedSelection() : Selection(sample.Slot, rows, choices);
                peer.Send(160, body);
                BoosterTcpTests.ExpectAck(peer, sample.Id, sample.Slot, rows.Select(row => (row.ItemId, (int)row.Count)).ToArray());
                BoosterTcpTests.Entry(peer.Expect(0, 14).Body, sample.Slot, -1, 0);
                byte[] avatars = peer.Expect(0, 14).Body;
                CheckAvatarRows(avatars, choices);
                expected.AddRange(choices);
                peer.Barrier();
                before = Snapshot(fixture);
                peer.Send(160, body); peer.Expect(1, 160, [0, 19, 0]); peer.Barrier();
                That(before == Snapshot(fixture), "装扮成功后重放耗尽源不重复创建核心、详情或UID");
            }
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            CheckAvatarRows(peer.SpecialContainers[1].Body, expected.ToArray(), full: true);
        }
        await fixture.StopAsync();
        That(fixture.Query("SELECT count(*) FROM inventory_avatar_details") is [[18L]] &&
            fixture.Query("SELECT count(*),count(DISTINCT i.item_uid) FROM character_inventory_items i JOIN inventory_avatar_details a USING(item_uid)") is [[18L, 18L]],
            "九件、八件、单件各自保存一份唯一核心与装扮详情");
        await RunMailAsync(source, root, resources, packages);

        ItemCore Core(int id) => InventoryRewardGrantService.Create(items.Get(id), shopExpiration: true);
    }

    static async Task RunMailAsync(ServerOptions source, string root, TcpTestResources resources, AvatarPackageCatalog packages)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "avatar-package-mail"), gm: true, resources: resources);
        var items = resources.ItemResources.Items;
        var rows = packages.Get(490705387).Categories.Single(row => row.Category == 0 && row.Subcategory == 0).Rows;
        var choices = rows.Select(row => new AvatarPackageChoice(row.ItemId, packages.GetAvatar(row.ItemId).Abilities.Max())).ToArray();
        var template = items.Get(rows[0].ItemId);
        var filled = Enumerable.Range(0, ItemSlotBoundService.AvatarCapacity).Select(slot => InventoryRewardGrantService.CreateItem(template,
            new(InventoryListType.Avatar, (short)slot), InventoryRewardGrantService.Create(template))).ToImmutableArray();
        var repo = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repo.Create(owner, new("AvatarMail", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = 15, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(15).Skills,
                Inventory = new([new(0, Bag(73), InventoryRewardGrantService.Create(items.Get(490705387), shopExpiration: true)), .. filled])
            });
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            string before = Snapshot(fixture);
            fixture.Sql("CREATE TRIGGER fail_avatar_mail BEFORE INSERT ON mailbox_messages BEGIN SELECT RAISE(ABORT,'avatar mail rollback'); END");
            peer.Send(518, Cera(73, choices)); peer.Closed();
            That(before == Snapshot(fixture), "装扮邮件失败不返回成功，源、完整实例、详情、UID、版本和审计整笔回滚");
            fixture.Sql("DROP TRIGGER fail_avatar_mail");
        }
        uint mailId;
        Dictionary<int, long> identities;
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Send(518, Cera(73, choices)); peer.Expect(1, 518, [1, 73, 0]);
            BoosterTcpTests.Entry(peer.Expect(0, 14).Body, 73, -1, 0);
            peer.Expect(0, 99, [1, 0]); peer.Barrier();
            peer.Send(96); peer.Expect(1, 96, [1, 0, 0]);
            var mailbox = MailboxTcpTests.ReadList(peer.Expect(0, 97).Body);
            That(mailbox.Details.Length == 1 && mailbox.Summaries.Length == choices.Length &&
                mailbox.Summaries.Select(row => row.ItemId).Order().SequenceEqual(choices.Select(choice => choice.ItemId).Order()),
                "满装扮页把九件完整套装作为同一永久系统邮件保存和展示");
            mailId = mailbox.Details.Single().Id;
            identities = fixture.Query("SELECT json_extract(i.item_core,'$.ItemId'),i.item_uid FROM character_inventory_items i JOIN mailbox_attachments a USING(item_uid) WHERE a.claimed=0")
                .ToDictionary(row => Convert.ToInt32(row[0]), row => Convert.ToInt64(row[1]));
            var persisted = fixture.Query("SELECT d.item_uid,json_extract(d.detail,'$.ExpireTime') FROM inventory_avatar_details d JOIN mailbox_attachments a USING(item_uid) WHERE a.claimed=0");
            That(persisted.Length == choices.Length && persisted.All(row => Convert.ToInt64(row[1]) == int.MaxValue),
                "Runtime默认开启商城调试后，每个邮件装扮详情保存2038截止；遗漏共享开关接线不能通过");
            foreach (var summary in mailbox.Summaries)
                That(U16(summary.Core, 11) == choices.Single(choice => choice.ItemId == summary.ItemId).Ability, "邮件摘要保留各部位所选属性编号");
            peer.Barrier();
            string before = Snapshot(fixture);
            peer.Send(518, Cera(73, choices)); peer.Expect(1, 518, [0, 4, 0]); peer.Barrier();
            That(before == Snapshot(fixture), "成功后重放不重复发装扮邮件");
        }
        await fixture.StopAsync();
        // 仅清理隔离测试角色的既有填充物，为原邮件领取制造空间；不修改实际玩家数据库。
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            for (short slot = 0; slot < choices.Length; slot++)
            {
                var character = repo.List(owner).Single();
                var item = character.Inventory.Find(new(InventoryListType.Avatar, slot))!;
                InventoryService.Commit(repo, owner, character, InventoryDeleteService.Plan(character.Inventory, item.Position, item.Core.Value, 1));
            }
        await fixture.StartAsync("reopened");
        using (var peer = Open(fixture))
        {
            peer.Select();
            var mailbox = MailboxTcpTests.ReadList(peer.Mailbox.Single());
            That(mailbox.Details.Single().Id == mailId && mailbox.Summaries.Length == choices.Length, "服务重建恢复原九件邮件，无额外赠送");
            var summaries = mailbox.Summaries;
            long claimStarted = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            peer.Send(95, Build(writer => { writer.Write(1); writer.Write(mailId); }));
            peer.Expect(1, 95, Build(writer =>
            {
                writer.Write((byte)1); writer.Write(summaries.Length);
                foreach (var summary in summaries) { writer.Write(summary.MailId); writer.Write(summary.ClaimId); writer.Write(0); }
            }));
            CheckAvatarRows(peer.Expect(0, 14).Body, choices, expectedExpiry: int.MaxValue, earliestUtcSeconds: claimStarted);
            MailboxTcpTests.ReadList(peer.Expect(0, 97).Body);
            peer.Barrier();
        }
        await fixture.StopAsync();
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var restored = repo.List(owner).Single();
            foreach (var choice in choices)
            {
                var item = restored.Inventory.Items.Single(item => item.Uid == identities[choice.ItemId]);
                That(item.Core.Durability == choice.Ability && item.Avatar is { ExpireTime: int.MaxValue } && item.Core.Value == item.Uid,
                    "领取后保留邮件原UID、核心选项和2038装扮详情期限");
                if (item.Core.ItemId == 401580026)
                    That(item.Avatar!.Socket0 == new AvatarSocket(239, -1) && item.Avatar.Socket1 == new AvatarSocket(239, -1), "双M孔经过邮件及领取仍完整保留");
            }
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "装扮礼盒邮件存档完整性");
    }

    static void CheckAvatarRows(byte[] body, AvatarPackageChoice[] choices, bool full = false,
        int expectedExpiry = 0, long? earliestUtcSeconds = null)
    {
        int header = full ? 5 : 3;
        int count = U16(body, full ? 3 : 1);
        That(body[0] == 1 && count == choices.Length && body.Length == header + count * 165, "装扮列表按完整165B核心及RAW孔位颜色投影");
        long received = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var choice in choices)
        {
            int at = Enumerable.Range(0, count).Select(index => header + index * 165).Single(at => U32(body, at + 2) == choice.ItemId);
            uint remaining = U32(body, at + 6);
            That(expectedExpiry == 0 ? remaining == 0 : earliestUtcSeconds is not null && remaining > 0 &&
                remaining >= expectedExpiry - received && remaining <= expectedExpiry - earliestUtcSeconds.Value,
                "永久装扮期限为0，有限装扮倒计时精确落在请求发送至回包读取之间的UTC范围");
            That(body[at + 10] == 0 && U16(body, at + 11) == choice.Ability && U32(body, at + 123) == 30 && U32(body, at + 157) == 4,
                "装扮Durability选项与完整附加块保留");
            if (choice.ItemId == 401580026)
                That(U16(body, at + 127) == 239 && U32(body, at + 129) == uint.MaxValue &&
                    U16(body, at + 133) == 239 && U32(body, at + 135) == uint.MaxValue, "type4皮肤投影两枚空M孔");
        }
    }
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static byte[] Selection(short slot, AvatarPackageRow[] rows, AvatarPackageChoice[] choices) => Build(writer =>
    {
        writer.Write(slot); writer.Write(1); writer.Write((byte)0); writer.Write((byte)0);
        foreach (var row in rows) writer.Write(row.ItemId);
        writer.Write((byte)choices.Length);
        foreach (var choice in choices) { writer.Write(choice.ItemId); writer.Write(choice.Ability); }
        writer.Write((byte)0);
    });
    static byte[] Cera(short slot, AvatarPackageChoice[] choices) => Build(writer =>
    {
        writer.Write(slot); writer.Write((byte)choices.Length);
        foreach (var choice in choices) { writer.Write(choice.ItemId); writer.Write(choice.Ability); }
    });
    static string Snapshot(TcpTestFixture fixture) => JsonSerializer.Serialize(new[]
    {
        fixture.Query("SELECT revision,gold FROM characters"),
        fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid"),
        fixture.Query("SELECT list_type,slot_index,item_uid FROM character_inventory_slots ORDER BY list_type,slot_index"),
        fixture.Query("SELECT item_uid,detail FROM inventory_avatar_details ORDER BY item_uid"),
        fixture.Query("SELECT count(*) FROM inventory_audit_log"), fixture.Query("SELECT count(*) FROM mailbox_messages"),
        fixture.Query("SELECT count(*) FROM mailbox_attachments"), fixture.Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'")
    });
}
