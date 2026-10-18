using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class CeraPackageAvatarOverflowTcpTests
{
    const int PackageId = 2683533;
    const short SourceSlot = 65;

    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var itemResources = resources.ItemResources;
        var definition = itemResources.Boosters.Get(PackageId);
        var totals = definition.PackageRewards.GroupBy(row => row.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Count));
        var choices = totals.Keys.Select(id => (ItemId: id,
            Option: itemResources.AvatarPackages.GetAvatar(id).Abilities.Min())).ToArray();
        var avatarIds = choices.Select(choice => choice.ItemId).ToHashSet();
        That(definition.Available && totals.Values.Sum() == 9 &&
            totals.Keys.All(id => itemResources.Items.Get(id).Kind == ItemKind.Avatar),
            "TCP夹具使用2683533真实九件装扮固定包");

        var sourceTemplate = itemResources.Items.Get(PackageId);
        byte job = EligibleJob(sourceTemplate);
        byte level = Math.Max((byte)1, sourceTemplate.MinimumLevel);
        CharacterSkill[] skills = resources.Characters.Get(job).Skills;
        if (sourceTemplate.RequiredSkill != 0 && skills.All(skill => skill.Id != sourceTemplate.RequiredSkill))
            skills = [.. skills, new(sourceTemplate.RequiredSkill, 1)];

        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "cera-package-avatar-mail"),
            gm: true, resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        Character partial;
        Character full;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            partial = repository.Create(owner, NewCharacter("CeraPart", freeSlots: 3));
            full = repository.Create(owner, NewCharacter("CeraFull", freeSlots: 0));
        }

        await fixture.StartAsync();
        BoosterTcpTests.SetExpirationDebug(fixture, true);
        long partialBaseline = MaxUid();
        using (var peer = Open(fixture))
        {
            peer.Select(partial.Slot); peer.Barrier();
            Send(peer, choices);
            peer.Expect(1, 518, [1, (byte)SourceSlot, 0]);
            BoosterTcpTests.Entry(peer.Expect(0, 14).Body, SourceSlot, -1, 0);
            CheckAvatarDelta(peer.Expect(0, 14).Body, choices, 3);
            peer.Expect(0, 99, [1, 0]); peer.Barrier();
            That(NewItems(partial.Id, partialBaseline) is { Length: 9 } partialItems &&
                partialItems.GroupBy(row => Convert.ToInt32(row[0]))
                    .All(group => totals[group.Key] == group.Count()) &&
                fixture.Query("""
                    SELECT count(*) FROM character_inventory_slots s
                    JOIN character_inventory_items i USING(item_uid)
                    WHERE i.character_id=$p0 AND i.item_uid>$p1 AND s.list_type=1
                    """, partial.Id, partialBaseline) is [[3L]] &&
                fixture.Query("""
                    SELECT count(*) FROM mailbox_attachments a
                    JOIN mailbox_messages m USING(mail_id)
                    WHERE m.recipient_id=$p0 AND a.claimed=0
                    """, partial.Id) is [[6L]],
                "2683533部分空位经518提交后三件入栏、六件进入同一角色邮件且总数为九");
            peer.Send(96); peer.Expect(1, 96, [1, 0, 0]);
            var mailbox = MailboxTcpTests.ReadList(peer.Expect(0, 97).Body, avatarIds);
            That(mailbox.Details.Length == 1 && mailbox.Summaries.Length == 6 &&
                mailbox.Summaries.Select(row => row.MailId).Distinct().Count() == 1,
                "部分空位的六件溢出装扮显示为同一封系统邮件");
            peer.Barrier();
        }

        long fullBaseline = MaxUid();
        uint fullMailId;
        using (var peer = Open(fixture))
        {
            peer.Select(full.Slot); peer.Barrier();
            Send(peer, choices);
            peer.Expect(1, 518, [1, (byte)SourceSlot, 0]);
            BoosterTcpTests.Entry(peer.Expect(0, 14).Body, SourceSlot, -1, 0);
            peer.Expect(0, 99, [1, 0]); peer.Barrier();
            That(NewItems(full.Id, fullBaseline) is { Length: 9 } fullItems &&
                fullItems.GroupBy(row => Convert.ToInt32(row[0]))
                    .All(group => totals[group.Key] == group.Count()) &&
                fixture.Query("""
                    SELECT count(*) FROM mailbox_attachments a
                    JOIN mailbox_messages m USING(mail_id)
                    WHERE m.recipient_id=$p0 AND a.claimed=0
                    """, full.Id) is [[9L]] &&
                fixture.Query("SELECT count(*) FROM mailbox_messages WHERE recipient_id=$p0", full.Id) is [[1L]],
                "2683533满装扮栏只持久化九件附件和一封邮件，不翻倍为十八件");
            peer.Send(96); peer.Expect(1, 96, [1, 0, 0]);
            var mailbox = MailboxTcpTests.ReadList(peer.Expect(0, 97).Body, avatarIds);
            That(mailbox.Details.Length == 1 && mailbox.Summaries.Length == 9 &&
                mailbox.Summaries.All(row => row.MailId == mailbox.Details[0].Id),
                "满装扮栏的九件真实附件均属于同一封邮件");
            fullMailId = mailbox.Details[0].Id;
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.Select(full.Slot);
            var restored = MailboxTcpTests.ReadList(peer.Mailbox.Single(), avatarIds);
            That(restored.Details.Single().Id == fullMailId && restored.Summaries.Length == 9 &&
                restored.Summaries.All(row => row.MailId == fullMailId) &&
                fixture.Query("SELECT count(*) FROM mailbox_messages WHERE recipient_id=$p0", full.Id) is [[1L]],
                "新连接重选角色仍恢复原一封邮件的九件附件，不重复投递");
            peer.Barrier();
        }

        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] &&
            fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "2683533部分空位、全满与重连后的存档完整");

        NewCharacter NewCharacter(string name, int freeSlots)
        {
            var filler = itemResources.Items.Get(definition.PackageRewards[0].ItemId);
            var filled = Enumerable.Range(0, ItemSlotBoundService.AvatarCapacity - freeSlots)
                .Select(slot => InventoryRewardGrantService.CreateItem(filler,
                    new(InventoryListType.Avatar, checked((short)slot)),
                    InventoryRewardGrantService.Create(filler, shopExpiration: true))).ToImmutableArray();
            return new(name, resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = job,
                Level = level,
                AcceptedStoryLevel = 1,
                Skills = skills,
                Inventory = new([new(0, new(InventoryListType.Main, SourceSlot),
                    InventoryRewardGrantService.Create(sourceTemplate, shopExpiration: true)), .. filled])
            };
        }

        long MaxUid() => Convert.ToInt64(fixture.Query(
            "SELECT coalesce(max(item_uid),0) FROM character_inventory_items").Single().Single());
        object[][] NewItems(ushort characterId, long afterUid) => fixture.Query("""
            SELECT json_extract(item_core,'$.ItemId'),item_uid
            FROM character_inventory_items
            WHERE character_id=$p0 AND item_uid>$p1
            ORDER BY item_uid
            """, characterId, afterUid);
    }

    static void CheckAvatarDelta(byte[] body, (int ItemId, byte Option)[] choices, int expected)
    {
        int count = U16(body, 1);
        That(body[0] == 1 && count == expected && body.Length == 3 + count * 165,
            "部分空位只向装扮栏发布实际入栏的三件完整条目");
        for (int index = 0; index < count; index++)
        {
            int at = 3 + index * 165;
            int itemId = checked((int)U32(body, at + 2));
            That(U16(body, at + 11) == choices.Single(choice => choice.ItemId == itemId).Option,
                "2683533入栏装扮保留518提交的属性选择");
        }
    }

    static byte EligibleJob(ItemTemplate template)
    {
        for (byte job = 0; job < 16; job++)
            if ((template.Jobs & (1 << job)) != 0)
                return job;
        throw new InvalidDataException("2683533没有可用职业");
    }

    static void Send(TcpPeer peer, (int ItemId, byte Option)[] choices) =>
        peer.Send(518, Build(writer =>
        {
            writer.Write(SourceSlot);
            writer.Write(checked((byte)choices.Length));
            foreach (var choice in choices)
            {
                writer.Write(choice.ItemId);
                writer.Write(choice.Option);
            }
        }));
}
