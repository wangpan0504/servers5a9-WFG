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
                // 旧追忆源保留资源职业门槛，夹具使用其允许的基础职业。
                Job = 0, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([.. Enumerable.Range(65, 8).Select(slot => new InventoryItem(0, Bag((short)slot),
                    InventoryRewardGrantService.Create(items.Get(1106), 1000))),
                    new(0, Bag(73), Core(490705385)), new(0, Bag(74), Core(490705425)), new(0, Bag(75), Core(490705426)),
                    new(0, Bag(76), Core(490008059)), new(0, Bag(77), Core(2683776)), new(0, Bag(78), Core(490702666))])
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
            OpenLegacy(490008059, 76);
            BoosterSelectionTcpTests.SendSelect(peer, 77, [2683778], category: 1);
            BoosterTcpTests.ExpectAck(peer, 2683776, 77, [(2683778, 1)]);
            byte[] delta = peer.Expect(0, 14).Body;
            BoosterTcpTests.Entry(delta, 77, -1, 0);
            short childSlot = Convert.ToInt16(fixture.Query("SELECT s.slot_index FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid) WHERE json_extract(i.item_core,'$.ItemId')=2683778").Single().Single());
            byte[] child = childSlot == 77 ? peer.Expect(0, 14).Body : delta;
            BoosterTcpTests.Entry(child, childSlot, 2683778, 1);
            That(U16(child, 1) == (childSlot == 77 ? 1 : 2), "追忆父选择只发一个实体子盒，未提前递归发装扮");
            peer.Barrier();
            OpenLegacy(2683778, childSlot);

            void OpenLegacy(int id, short slot)
            {
                var rows = packages.Get(id).Categories.Single(row => row.Category == 0 && row.Subcategory == 0).Rows.ToArray();
                var choices = rows.Select(row => new AvatarPackageChoice(row.ItemId, packages.GetAvatar(row.ItemId).Abilities.Max())).ToArray();
                string before = Snapshot(fixture);
                peer.Send(160, Selection(slot, rows, [.. choices[..^1], new(choices[^1].ItemId, 255)]));
                peer.Expect(1, 160, [0, 19, 0]); peer.Barrier();
                That(before == Snapshot(fixture), "追忆完整奖励中任一能力非法时保留源、全部实例、UID和审计，连接可继续");
                peer.Send(160, Selection(slot, rows, choices));
                BoosterTcpTests.ExpectAck(peer, id, slot, rows.Select(row => (row.ItemId, (int)row.Count)).ToArray());
                BoosterTcpTests.Entry(peer.Expect(0, 14).Body, slot, -1, 0);
                CheckAvatarRows(peer.Expect(0, 14).Body, choices);
                expected.AddRange(choices);
                peer.Barrier();
                before = Snapshot(fixture);
                peer.Send(160, Selection(slot, rows, choices)); peer.Expect(1, 160, [0, 19, 0]); peer.Barrier();
                That(before == Snapshot(fixture), "追忆源只消费一次，耗尽重放不重复发八件装扮");
            }
        }
        var constant = OpenConstantMagnitude(fixture, packages, repo);
        expected.Add(new(39952, 2));
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            byte[] full = peer.SpecialContainers[1].Body;
            CheckAvatarRows(full, expected.ToArray(), full: true);
            Bytes(AvatarRow(full, 39952, full: true), constant.Row,
                "真实39952重连恢复原165B完整核心、永久期限、五孔和颜色，不重建实例");
        }
        await fixture.StopAsync();
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            That(repo.List(owner).Single().Inventory.Items.Single(item => item.Uid == constant.Item.Uid) == constant.Item,
                "真实39952重连后完整UID、位置、核心和装扮详情与提交实例一致");
        That(File.ReadLines(Path.Combine(fixture.Options.LogDirectory, "server.jsonl")).Count(line =>
        {
            using var entry = JsonDocument.Parse(line); var data = entry.RootElement;
            return data.GetProperty("Kind").GetString() == "closed" &&
                data.GetProperty("Detail").GetProperty("Reason").GetString() == "SqliteException" &&
                data.GetProperty("Detail").GetProperty("Message").GetString() == "SQLite Error 19: 'constant avatar late rollback'.";
        }) == 1, "真实39952断线精确来自所注入的晚SQL审计故障一次");
        That(fixture.Query("SELECT count(*) FROM inventory_avatar_details") is [[35L]] &&
            fixture.Query("SELECT count(*),count(DISTINCT i.item_uid) FROM character_inventory_items i JOIN inventory_avatar_details a USING(item_uid)") is [[35L, 35L]],
            "既有三十四件及真实39952各自保存一份唯一核心与装扮详情");
        await RunMailAsync(source, root, resources, packages);
        await RunObservedSelectionsAsync(source, root, resources);

        ItemCore Core(int id) => InventoryRewardGrantService.Create(items.Get(id), shopExpiration: true);
    }

    public static async Task RunObservedSelectionsAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await GiftCompatibilityTcpTests.RunAsync(source, root, resources);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "observed-selections"), gm: true, resources: resources);
        var items = resources.ItemResources.Items;
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repository.Create(owner, new("ObservedBox", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = 15, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(15).Skills,
                Inventory = new([new(0, Bag(68), Core(490705413)), new(0, Bag(69), Core(490705389)), new(0, Bag(70), Core(490705390)), new(0, Bag(71), Core(490705389)), new(0, Bag(72), Core(490705390)),
                    new(0, Bag(73), Core(490704635)), new(0, Bag(74), Core(490704635))])
            });
        await fixture.StartAsync();
        BoosterTcpTests.SetExpirationDebug(fixture, true);
        foreach (var failure in new[] {
            (Table: "inventory_avatar_details", Body: "450001000000000014C7EF170000"),
            (Table: "inventory_avatar_details", Body: "490001000000000002C7EF170000"),
            (Table: "inventory_creature_details", Body: "4600010000000000ECA0E6170000") })
        {
            string unchanged;
            fixture.Sql($"CREATE TRIGGER fail_selected_detail BEFORE INSERT ON {failure.Table} BEGIN SELECT RAISE(ABORT,'selected detail rollback'); END");
            using (var peer = Open(fixture))
            {
                peer.Select(); peer.Barrier();
                unchanged = State();
                peer.Send(160, Convert.FromHexString(failure.Body)); peer.Closed();
            }
            That(unchanged == State(), "光环或宠物详情写入失败时源、实例、UID、版本和审计全部回滚");
            fixture.Sql("DROP TRIGGER fail_selected_detail");
        }
        byte[] committedPetDetails = [];
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            var packages = resources.ItemResources.AvatarPackages;
            That(items.Get(415600033).Expiration is { AbsoluteTime: 0, Days: 0 } &&
                packages.GetAvatar(415600033).Types[4].Days == 0 &&
                packages.Get(490705413).Categories.Single(row => row.Category == 9 && row.Subcategory == 0)
                    .Rows.Single(row => row.ItemId == 415600033).Type == 4 &&
                items.Get(401590036).Expiration is { AbsoluteTime: 0, Days: 0 } &&
                packages.GetAvatar(401590036).Types.IsEmpty,
                "武器礼盒选择永久类型4，光环无期限表；礼盒期限不能冒充奖励期限");
            string before = State();
            peer.Send(160, [68]); peer.Expect(1, 160, [0, 19, 0]); peer.Barrier();
            That(before == State(), "截断开包请求明确拒绝且不改变数据库，连接保持可用");
            foreach (var sample in new[] {
                (Id: 490705413, Slot: (short)68, Reward: 415600033, Body: "4400010000000900A18DC5180000"),
                (Id: 490705389, Slot: (short)69, Reward: 401590036, Body: "450001000000000014C7EF170000"),
                (Id: 490705390, Slot: (short)70, Reward: 400990444, Body: "4600010000000000ECA0E6170000"),
                (Id: 490705389, Slot: (short)71, Reward: 401590037, Body: "470001000000000015C7EF170000"),
                (Id: 490705390, Slot: (short)72, Reward: 400990445, Body: "4800010000000000EDA0E6170000"),
                (Id: 490704635, Slot: (short)73, Reward: 401590018, Body: "490001000000000002C7EF170000"),
                (Id: 490704635, Slot: (short)74, Reward: 401590019, Body: "4A0001000000000003C7EF170000") })
            {
                byte[] body = Convert.FromHexString(sample.Body);
                peer.Send(160, body);
                BoosterTcpTests.ExpectAck(peer, sample.Id, sample.Slot, [(sample.Reward, 1)]);
                if (sample.Id == 490705390)
                {
                    byte[] details = peer.Expect(0, 105).Body;
                    int expectedCount = committedPetDetails.Length == 0 ? 1 : committedPetDetails[0] + 1;
                    That(details[0] == expectedCount && (committedPetDetails.Length == 0 ||
                        details.AsSpan(1, committedPetDetails.Length - 1).SequenceEqual(committedPetDetails.AsSpan(1))),
                        "宠物奖励先发送全部已提交详情，第二次发放保留第一只宠物原详情");
                    committedPetDetails = details;
                }
                BoosterTcpTests.Entry(peer.Expect(0, 14).Body, sample.Slot, -1, 0);
                byte[] changed = peer.Expect(0, 14).Body;
                if (sample.Id != 490705390)
                    CheckAvatarRows(changed, [new(sample.Reward, 0)]);
                else
                    That(changed[0] == 7 && U16(changed, 1) == 1 && changed.Length == 126 && U32(changed, 5) == sample.Reward &&
                        U32(changed, 9) > 0, "宠物专属页收到完整123B核心和已提交UID");
                peer.Barrier();
                before = State();
                peer.Send(160, body); peer.Expect(1, 160, [0, 19, 0]); peer.Barrier();
                That(before == State(), "真实请求重放不重复发放、不改详情、版本或审计");
            }
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            CheckAvatarRows(peer.SpecialContainers[1].Body,
                [new(415600033, 0), new(401590036, 0), new(401590037, 0), new(401590018, 0), new(401590019, 0)], full: true);
            That(peer.SpecialContainers.Single(packet => packet.Opcode == 105).Body.SequenceEqual(committedPetDetails),
                "重连仍恢复两个选择各自的完整宠物详情");
        }
        await fixture.StopAsync();
        That(fixture.Query("SELECT count(*) FROM inventory_avatar_details") is [[5L]] &&
            fixture.Query("SELECT count(*) FROM inventory_creature_details") is [[2L]] &&
            fixture.Query("SELECT count(*),count(DISTINCT item_uid) FROM character_inventory_items") is [[7L, 7L]],
            "使徒与金秋光环各两项、两种宠物及武器装扮保存七个唯一实例，源礼盒均已扣除");
        foreach (int id in new[] { 401590018, 401590019 })
        {
            string json = (string)fixture.Query("SELECT d.detail FROM inventory_avatar_details d JOIN character_inventory_items i USING(item_uid) WHERE json_extract(i.item_core,'$.ItemId')=$p0", id).Single().Single()!;
            That(JsonSerializer.Deserialize<AvatarDetail>(json) ==
                new AvatarDetail { Socket0 = new(239, -1), Socket1 = new(239, -1) },
                "金秋光环完整详情持久化两个多色空孔，其余孔及期限保持零");
        }
        ItemCore Core(int id) => InventoryRewardGrantService.Create(items.Get(id), shopExpiration: true);
        string State() => Snapshot(fixture) + JsonSerializer.Serialize(fixture.Query("SELECT item_uid,detail FROM inventory_creature_details ORDER BY item_uid"));
    }

    static async Task RunMailAsync(ServerOptions source, string root, TcpTestResources resources, AvatarPackageCatalog packages)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "avatar-package-mail"), gm: true, resources: resources);
        var items = resources.ItemResources.Items;
        var rows = packages.Get(490705387).Categories.Single(row => row.Category == 0 && row.Subcategory == 0).Rows;
        var choices = rows.Select(row => new AvatarPackageChoice(row.ItemId, packages.GetAvatar(row.ItemId).Abilities.Max())).ToArray();
        var avatarIds = choices.Select(choice => choice.ItemId).ToHashSet();
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
        // 490702666不允许原邮件角色的Job15；同一fixture保留原角色并增加合法Job0满页角色。
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repo.Create(owner, new("ConstantFull", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = 0, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, Bag(74), InventoryRewardGrantService.Create(items.Get(490702666), shopExpiration: true)), .. filled])
            });
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(1); peer.Barrier();
            string before = Snapshot(fixture);
            peer.Send(160, Selection(74, [ConstantRow(packages)], [new(39952, 2)]));
            peer.Expect(1, 160, [0, 4, 0]); peer.Barrier();
            That(before == Snapshot(fixture), "真实39952合法选项遇210格满页明确拒绝4，源、实例、UID、审计和邮件均不变");
        }
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
            var mailbox = MailboxTcpTests.ReadList(peer.Expect(0, 97).Body, avatarIds);
            That(mailbox.Details.Length == 1 && mailbox.Summaries.Length == choices.Length &&
                mailbox.Summaries.Select(row => row.ItemId).Order().SequenceEqual(choices.Select(choice => choice.ItemId).Order()),
                "满装扮页把九件完整套装作为同一永久系统邮件保存和展示");
            mailId = mailbox.Details.Single().Id;
            identities = fixture.Query("SELECT json_extract(i.item_core,'$.ItemId'),i.item_uid FROM character_inventory_items i JOIN mailbox_attachments a USING(item_uid) WHERE a.claimed=0")
                .ToDictionary(row => Convert.ToInt32(row[0]), row => Convert.ToInt64(row[1]));
            var persisted = fixture.Query("SELECT d.item_uid,json_extract(d.detail,'$.ExpireTime') FROM inventory_avatar_details d JOIN mailbox_attachments a USING(item_uid) WHERE a.claimed=0");
            That(persisted.Length == choices.Length && persisted.All(row => Convert.ToInt64(row[1]) == 0),
                "Runtime默认只忽略绝对日期，原永久装扮邮件详情仍为0");
            foreach (var summary in mailbox.Summaries)
                CheckAvatarMailSummary(summary, choices);
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
                var character = repo.List(owner).Single(character => character.Name == "AvatarMail");
                var item = character.Inventory.Find(new(InventoryListType.Avatar, slot))!;
                InventoryService.Commit(repo, owner, character, InventoryDeleteService.Plan(character.Inventory, item.Position, item.Core.Value, 1));
            }
        await fixture.StartAsync("reopened");
        using (var peer = Open(fixture))
        {
            peer.Select();
            var mailbox = MailboxTcpTests.ReadList(peer.Mailbox.Single(), avatarIds);
            That(mailbox.Details.Single().Id == mailId && mailbox.Summaries.Length == choices.Length, "服务重建恢复原九件邮件，无额外赠送");
            var summaries = mailbox.Summaries;
            foreach (var summary in summaries) CheckAvatarMailSummary(summary, choices);
            peer.Send(95, Build(writer => { writer.Write(1); writer.Write(mailId); }));
            peer.Expect(1, 95, Build(writer =>
            {
                writer.Write((byte)1); writer.Write(summaries.Length);
                foreach (var summary in summaries) { writer.Write(summary.MailId); writer.Write(summary.ClaimId); writer.Write(0); }
            }));
            CheckAvatarRows(peer.Expect(0, 14).Body, choices);
            peer.Barrier();
            peer.Send(96); peer.Expect(1, 96, [1, 0, 0]);
            MailboxTcpTests.ReadList(peer.Expect(0, 97).Body, avatarIds);
            peer.Barrier();
        }
        await fixture.StopAsync();
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var restored = repo.List(owner).Single(character => character.Name == "AvatarMail");
            foreach (var choice in choices)
            {
                var item = restored.Inventory.Items.Single(item => item.Uid == identities[choice.ItemId]);
                That(item.Core.Durability == choice.Ability && item.Avatar is { ExpireTime: 0 } && item.Core.Value == item.Uid,
                    "领取后保留邮件原UID、核心选项和原永久期限");
                if (item.Core.ItemId == 401580026)
                    That(item.Avatar!.Socket0 == new AvatarSocket(239, -1) && item.Avatar.Socket1 == new AvatarSocket(239, -1), "双M孔经过邮件及领取仍完整保留");
            }
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "装扮礼盒邮件存档完整性");
    }

    static (byte[] Row, InventoryItem Item) OpenConstantMagnitude(TcpTestFixture fixture,
        AvatarPackageCatalog packages, CharacterRepository repository)
    {
        var row = ConstantRow(packages);
        var choice = new AvatarPackageChoice(39952, 2);
        byte[] body = Selection(78, [row], [choice]);
        long sourceUid = Convert.ToInt64(fixture.Query(
            "SELECT item_uid FROM character_inventory_slots WHERE list_type=0 AND slot_index=78").Single().Single());
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            string before = Snapshot(fixture);
            foreach (var invalid in new AvatarPackageChoice[][] { [], [new(39952, 4)], [new(39952, 255)], [new(39953, 2)] })
            {
                peer.Send(160, Selection(78, [row], invalid)); peer.Expect(1, 160, [0, 19, 0]); peer.Barrier();
                That(before == Snapshot(fixture), "真实39952缺属性、越界属性或错配奖励均拒绝19，不消费源或改变任何资产");
            }
            fixture.Sql("CREATE TRIGGER fail_constant_avatar BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'constant avatar late rollback'); END");
            peer.Send(160, body); peer.Closed();
            That(before == Snapshot(fixture), "真实39952写入核心、详情和槽后的晚审计故障无成功ACK，完整资产、revision和UID序列回滚");
            fixture.Sql("DROP TRIGGER fail_constant_avatar");
        }
        byte[] committed;
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            peer.Send(160, body);
            BoosterTcpTests.ExpectAck(peer, 490702666, 78, [(39952, 1)]);
            BoosterTcpTests.Entry(peer.Expect(0, 14).Body, 78, -1, 0);
            byte[] avatars = peer.Expect(0, 14).Body;
            CheckAvatarRows(avatars, [choice]);
            committed = AvatarRow(avatars, 39952);
            byte[] raw = new byte[42]; raw[0] = 30; raw[34] = 4;
            Bytes(committed.AsSpan(123, 42), raw, "真实39952永久type3完整五孔及颜色均为零，只保留RAW30和RAW4长度标记");
            peer.Barrier();
            string before = Snapshot(fixture);
            peer.Send(160, body); peer.Expect(1, 160, [0, 19, 0]); peer.Barrier();
            That(before == Snapshot(fixture), "真实490702666耗尽重放不重复发39952，不改变完整实例、版本或UID序列");
        }
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var item = repository.List(owner).Single().Inventory.Items.Single(item => item.Core.ItemId == 39952);
            That(item.Position.List == InventoryListType.Avatar && item.Uid > 0 && item.Uid != sourceUid &&
                item.Core == new ItemCore(39952, ItemKind.Avatar, checked((int)item.Uid)) { Durability = 2 } &&
                item.Avatar == new AvatarDetail() &&
                fixture.Query("SELECT item_uid FROM character_inventory_items WHERE item_uid=$p0", sourceUid).Length == 0 &&
                fixture.Query("SELECT count(*),count(DISTINCT item_uid) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=39952") is [[1L, 1L]],
                "真实39952完整核心仅属性2和UID非零，永久无孔详情完整持久化，源UID注销且新UID唯一");
            return (committed, item);
        }
    }

    static AvatarPackageRow ConstantRow(AvatarPackageCatalog packages)
    {
        var definition = packages.Get(490702666);
        var row = definition.Categories.Single(category => category.Category == 0 && category.Subcategory == 0)
            .Rows.Single(row => row.ItemId == 39952);
        That(definition.SelectionCount == 1 && row == new AvatarPackageRow(39952, 1, 3, 0),
            "真实490702666的0/0分类选择一件39952，保持原数量、永久type3和默认预览属性0");
        return row;
    }

    static byte[] AvatarRow(byte[] body, int itemId, bool full = false)
    {
        int header = full ? 5 : 3;
        int at = Enumerable.Range(0, U16(body, full ? 3 : 1)).Select(index => header + index * 165)
            .Single(at => U32(body, at + 2) == itemId);
        return body.AsSpan(at, 165).ToArray();
    }

    static void CheckAvatarMailSummary(MailboxTcpTests.Summary summary, AvatarPackageChoice[] choices)
    {
        var extra = summary.Extra ?? throw new InvalidDataException("装扮邮件缺少RAW附加块");
        That(U16(summary.Core, 11) == choices.Single(choice => choice.ItemId == summary.ItemId).Ability,
            "邮件摘要保留各部位所选属性编号");
        That(extra.Length == 42 && U32(extra, 0) == 30 && U32(extra, 34) == 4 && U16(extra, 38) == 0 && U16(extra, 40) == 0,
            "邮件装扮摘要完整消费两RAW的42B孔位/颜色附加块");
        for (int socket = 0; socket < 5; socket++)
        {
            bool skinSocket = summary.ItemId == 401580026 && socket < 2;
            That(U16(extra, 4 + socket * 6) == (skinSocket ? 239 : 0) &&
                U32(extra, 6 + socket * 6) == (skinSocket ? uint.MaxValue : 0),
                "邮件摘要保留皮肤双空M孔及其他部位完整未开放孔位");
        }
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
            if (choice.ItemId is 401590018 or 401590019)
                Bytes(body.AsSpan(at + 123, 34), Convert.FromHexString(
                    "1E000000EF00FFFFFFFFEF00FFFFFFFF000000000000000000000000000000000000"),
                    "金秋光环发奖和重连均下发RAW30：双M孔、空徽章-1、其余三孔关闭");
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
