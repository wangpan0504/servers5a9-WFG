using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class CeraPackageTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var items = resources.ItemResources;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "cera-package"), gm: true, resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repo.Create(owner, new("CeraPackage", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = 15, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(15).Skills,
                Inventory = new([new(0, Bag(3), Core(490705386)), new(0, Bag(4), Core(490705385)),
                    new(0, Bag(5), Core(490705483)), new(0, Bag(6), Core(7542)), new(0, Bag(7), Core(490704193))])
            });
        await fixture.StartAsync();
        BoosterTcpTests.SetExpirationDebug(fixture, true);
        using (var peer = Open(fixture))
        {
            Send(peer, 3);
            peer.Expect(1, 518, [0, 4, 0]);
            peer.Select();
            string before = Snapshot();
            foreach (byte[] malformed in new byte[][] { [], [3, 0], [3, 0, 1], [3, 0, 0, 0], [255, 255, 0],
                [3, 0, 1, 0, 0, 0, 0, 0], [3, 0, 2, 1, 0, 0, 0, 0, 1, 0, 0, 0, 1] })
            {
                peer.Send(518, malformed);
                peer.Expect(1, 518, [0, 4, 0]);
            }
            Send(peer, 3, [(1042, 255)]);
            peer.Expect(1, 518, [0, 4, 0]);
            Send(peer, 6);
            peer.Expect(1, 518, [0, 4, 0]);
            peer.Send(160, Build(w => { w.Write((short)3); w.Write(1); }));
            peer.Expect(1, 160, [0, 19, 0]);
            peer.Barrier();
            That(before == Snapshot(), "非法礼包请求、非空时装选择及错误入口不消耗源、不产生奖励或审计");

            Send(peer, 3);
            peer.Expect(1, 518, [1, 3, 0]);
            byte[] delta = peer.Expect(0, 14).Body;
            That(delta.Length == 3 + 4 * 123 && U16(delta, 1) == 4, "固定礼包一次提交后发布源格和三项完整奖励");
            BoosterTcpTests.Entry(delta, 3, -1, 0);
            for (short slot = 65; slot <= 67; slot++)
                BoosterTcpTests.Entry(delta, slot, 490705083 + slot - 65, 1);
            peer.Barrier();
            before = Snapshot();
            Send(peer, 3);
            peer.Expect(1, 518, [0, 4, 0]);
            peer.Barrier();
            That(before == Snapshot(), "耗尽源重放不再产生固定包奖励");

            foreach (short sourceSlot in new short[] { 4, 5 })
            {
                Send(peer, sourceSlot);
                peer.Expect(1, 518, [1, (byte)sourceSlot, 0]);
                byte[] rewards = peer.Expect(0, 14).Body;
                That(U16(rewards, 1) == 15, "众神降世主礼包一次发布14项固定奖励和源格");
                BoosterTcpTests.Entry(rewards, sourceSlot, -1, 0);
                foreach (var expected in new[] { (490705387, 1), (490705389, 1), (490705390, 1), (490705391, 1),
                    (490705392, 1), (490705393, 1), (490705394, 1), (490705395, 3), (490705396, 1), (490705415, 1),
                    (490705397, 1), (490705398, 1), (490705399, 1), (490705400, 3) })
                {
                    int at = Enumerable.Range(0, U16(rewards, 1)).Select(index => 3 + index * 123)
                        .Single(at => U32(rewards, at + 2) == expected.Item1);
                    That(U32(rewards, at + 6) == expected.Item2 * (sourceSlot - 3), "礼包奖励累计数量来自固定PVF清单");
                }
            }
            peer.Barrier();
            var costumeChoices = new (int ItemId, byte Option)[]
            {
                (401550106, 4), (401560063, 4), (401570036, 2), (401520056, 2), (401500096, 9),
                (401530044, 4), (401510075, 0), (401540071, 0), (401580002, 0)
            };
            string costumeBefore = Snapshot();
            Send(peer, 7, [(1042, 4), .. costumeChoices[1..]]);
            peer.Expect(1, 518, [0, 4, 0]);
            peer.Barrier();
            That(costumeBefore == Snapshot(), "错时装选择不消耗夏日礼包");
            Send(peer, 7, costumeChoices);
            peer.Expect(1, 518, [1, 7, 0]);
            byte[] costumeMain = peer.Expect(0, 14).Body;
            BoosterTcpTests.Entry(costumeMain, 7, -1, 0);
            byte[] costumeAvatars = peer.Expect(0, 14).Body;
            That(costumeAvatars[0] == 1 && U16(costumeAvatars, 1) == 9 && costumeAvatars.Length == 3 + 9 * 165,
                "含时装固定礼包一次提交时装栏九件完整条目");
            foreach (var choice in costumeChoices)
            {
                int at = Enumerable.Range(0, 9).Select(index => 3 + index * 165)
                    .Single(at => U32(costumeAvatars, at + 2) == choice.ItemId);
                That(U16(costumeAvatars, at + 11) == choice.Option, "518时装option写入Durability " + choice.ItemId);
            }
            peer.Barrier();
            before = Snapshot();
            Send(peer, 7, costumeChoices);
            peer.Expect(1, 518, [0, 4, 0]);
            peer.Barrier();
            That(before == Snapshot(), "耗尽后重放夏日礼包不再发奖");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            var saved = repo.List(owner).Single();
            That(saved.Inventory.Items.All(item => item.Core.ItemId is not (490705385 or 490705386 or 490705483 or 490704193)) &&
                saved.Inventory.Items.Where(item => item.Core.ItemId == 490705400).Sum(item => item.Core.Count) == 6 &&
                saved.Inventory.Items.Count(item => item.Position.List == InventoryListType.Avatar) == 9,
                "重登恢复耗尽源、固定奖励和九件时装");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "固定礼包存档完整性");
        await RunOverflowAsync(source, root, resources);
        await CeraPackageAvatarOverflowTcpTests.RunAsync(source, root, resources);

        ItemCore Core(int id) => InventoryRewardGrantService.Create(items.Items.Get(id), shopExpiration: true);
        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT revision,gold,revive_coins FROM characters")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM mailbox_messages")) +
            JsonSerializer.Serialize(fixture.Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'"));
    }

    static async Task RunOverflowAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "cera-package-mail"), gm: true, resources: resources);
        var items = resources.ItemResources.Items;
        var filled = Enumerable.Range(65, 56).Select(slot => new InventoryItem(0, Bag((short)slot),
            InventoryRewardGrantService.Create(items.Get(1106), 1000))).ToImmutableArray();
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            new CharacterRepository(fixture.Database).Create(owner, new("CeraMail", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = 15, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(15).Skills,
                Inventory = new([new(0, Bag(3), InventoryRewardGrantService.Create(items.Get(490705386), shopExpiration: true)), .. filled])
            });
        await fixture.StartAsync();
        BoosterTcpTests.SetExpirationDebug(fixture, true);
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_package_mail BEFORE INSERT ON mailbox_messages BEGIN SELECT RAISE(ABORT,'package rollback'); END");
            Send(peer, 3); peer.Closed();
            That(before == Snapshot(), "邮件写入失败不发送开包成功，源、奖励、UID、版本和审计完整回滚");
            fixture.Sql("DROP TRIGGER fail_package_mail");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            Send(peer, 3);
            peer.Expect(1, 518, [1, 3, 0]);
            BoosterTcpTests.Entry(peer.Expect(0, 14).Body, 3, -1, 0);
            peer.Expect(0, 99, [1, 0]); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_inventory_slots WHERE list_type=0") is [[56L]] &&
                fixture.Query("SELECT count(*),count(DISTINCT item_uid) FROM character_inventory_items") is [[59L, 59L]] &&
                fixture.Query("SELECT count(*) FROM mailbox_attachments WHERE claimed=0 AND item_uid IS NOT NULL") is [[3L]] &&
                fixture.Query("SELECT count(*),system_mail FROM mailbox_messages WHERE expires_at IS NULL") is [[1L, 1L]],
                "满消耗品页成功开包并以一封永久系统邮件承接三项奖励");
            peer.Send(96); peer.Expect(1, 96, [1, 0, 0]);
            var page = MailboxTcpTests.ReadList(peer.Expect(0, 97).Body);
            That(page.Details.Length == 1 && page.Summaries.Select(row => row.ItemId).Order()
                .SequenceEqual(new[] { 490705083, 490705084, 490705085 }),
                "溢出奖励通过既有邮件协议展示同一邮件的三项真实附件");
            string before = Snapshot();
            Send(peer, 3); peer.Expect(1, 518, [0, 4, 0]); peer.Barrier();
            That(before == Snapshot(), "满包成功后重放不重复发邮件");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            var restored = MailboxTcpTests.ReadList(peer.Mailbox.Single());
            That(restored.Details.Length == 1 && restored.Summaries.Length == 3, "重登恢复同一礼包溢出邮件与三项附件");
            peer.Barrier();
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "礼包邮件存档完整性");
        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT revision FROM characters")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM mailbox_messages")) +
            JsonSerializer.Serialize(fixture.Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'"));
    }

    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static void Send(TcpPeer peer, short slot, (int ItemId, byte Option)[]? choices = null)
        => peer.Send(518, Build(w =>
        {
            w.Write(slot); w.Write(checked((byte)(choices?.Length ?? 0)));
            foreach (var choice in choices ?? []) { w.Write(choice.ItemId); w.Write(choice.Option); }
        }));
}
