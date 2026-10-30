using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Mailbox;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class MailboxTcpTests
{
    static void PreparePetClaimRole(CharacterRepository repository, AccountOwners.Lease owner, TcpTestResources resources)
    {
        var template = resources.ItemResources.Items.Get(63023);
        repository.Create(owner, new("MailPetFull", resources.Towns.Room, resources.Towns.InitialReturn)
        {
            Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
            Inventory = new([.. Enumerable.Range(1, 139).Select(slot => InventoryRewardGrantService.CreateItem(template,
                new(InventoryListType.Pet, (short)slot), InventoryRewardGrantService.Create(template)))], Gold: 1000)
        });
    }

    static void VerifyPetClaims(TcpTestFixture fixture, TcpTestResources resources)
    {
        var repository = new CharacterRepository(fixture.Database);
        var itemResources = resources.ItemResources;
        var service = new MailboxService(repository, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
        var template = itemResources.Items.Get(63022);
        var egg = InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Pet, 0), InventoryRewardGrantService.Create(template));
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var normal = service.Deliver(new("pet-complete", service.FindRecipient("MailRecv")!, "系统", "两蛋整封领取", 0, [egg, egg], now));
        var limited = service.Deliver(new("pet-prefix", service.FindRecipient("MailPetFull")!, "系统", "一格空间两只蛋", 0, [egg, egg], now));
        That(normal.Accepted && limited.Accepted, "两个蛋创建独立托管附件，不以模板相同合并实例");
        var remainingUids = fixture.Query("SELECT item_uid FROM mailbox_attachments WHERE mail_id=$p0 ORDER BY ordinal", limited.MailId)
            .Select(row => (long)row[0]).ToArray();
        That(remainingUids.Length == 2 && remainingUids[0] != remainingUids[1], "满包样本有两个不同的宠物UID");

        Summary[] pending;
        using (var peer = Open(fixture, "20001"))
        {
            peer.Select(prepared: true);
            var page = ReadList(peer.Mailbox.Single(), creatureItems: new HashSet<int> { 63022 });
            var rows = page.Summaries.Where(row => row.MailId == normal.MailId).ToArray();
            That(rows.Length == 2 && rows.All(row => row.ItemId == 63022 && row.Extra is { Length: 5 }),
                "初始邮件包含两只蛋及本版Creature摘要前缀");
            Extract(peer, rows.Select(row => row.ClaimId).ToArray());
            ExpectClaim(peer, rows);
            var details = peer.Expect(0, 105).Body;
            That(details[0] == 2, "整封领取先发布两只宠物的完整详情");
            using (var reader = new BinaryReader(new MemoryStream(details)))
            {
                reader.ReadByte();
                for (int index = 0; index < 2; index++)
                {
                    int petUid = reader.ReadInt32();
                    reader.ReadByte();
                    byte mode = reader.ReadByte();
                    reader.ReadInt32();
                    if (mode == 1) reader.ReadBytes(2);
                    reader.ReadByte();
                    string name = CharacterName.Encoding.GetString(reader.ReadBytes(reader.ReadInt32()));
                    reader.ReadByte();
                    That(name.Length > 0 && name == CreatureRules.DefaultName(itemResources.Creatures.Get(63022)!.Name) &&
                        fixture.Query("SELECT json_extract(detail,'$.Name') FROM inventory_creature_details WHERE item_uid=$p0", petUid)
                            is [[string stored]] && stored == name,
                        "新授予宠物邮件领取的105含非空资源名，名称与持久化详情一致");
                }
                That(reader.BaseStream.Position == reader.BaseStream.Length, "领取宠物详情字段完整对齐");
            }
            var delta = peer.Expect(0, 14).Body;
            That(delta[0] == 7 && U16(delta, 1) == 2, "空间足够时一次领取两只蛋，两个Pet槽均同步");
            peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM mailbox_attachments WHERE mail_id=$p0 AND claimed=1", normal.MailId) is [[2L]],
                "整封的两个附件均在同一次提交中领完");
            peer.Send(96); peer.Expect(1, 96, [1, 0, 0]);
            That(ReadList(peer.Expect(0, 97).Body).Summaries.Single(row => row.MailId == normal.MailId).ItemId == 0,
                "领取后的完整列表只在重新打开时下发，不重置当前窗口对象");
            peer.Send(7); peer.Batch((1, 7), (0, 2));
            peer.Select(1);
            pending = ReadList(peer.Mailbox.Single(), creatureItems: new HashSet<int> { 63022 }).Summaries
                .Where(row => row.MailId == limited.MailId).ToArray();
            That(pending.Length == 2, "仅剩一个Pet空格时仍完整显示两个未领取附件");
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_mail_prefix BEFORE UPDATE OF state ON mailbox_messages BEGIN SELECT RAISE(ABORT,'prefix failure'); END");
            Extract(peer, pending.Select(row => row.ClaimId).ToArray());
            peer.Closed();
            That(Snapshot() == before, "部分成功规划遇晚期SQL失败也必须整体回滚，不领走第一只蛋或发送成功");
            fixture.Sql("DROP TRIGGER fail_mail_prefix");
        }
        using (var peer = Open(fixture, "20001"))
        {
            peer.Select(1);
            Extract(peer, pending.Select(row => row.ClaimId).ToArray());
            peer.Expect(1, 95, Build(w =>
            {
                w.Write((byte)1); w.Write(2);
                w.Write(limited.MailId); w.Write(pending[0].ClaimId); w.Write(0);
                w.Write(limited.MailId); w.Write(pending[1].ClaimId); w.Write(4);
            }));
            That(peer.Expect(0, 105).Body[0] == 140, "成功前缀补足Pet页，不把失败附件放进详情投影");
            var delta = peer.Expect(0, 14).Body;
            That(delta[0] == 7 && U16(delta, 1) == 1 && U16(delta, 3) == 0 && U32(delta, 9) == remainingUids[0],
                "容量不足只同步实际入包的第一只蛋，保留它的原UID");
            peer.Barrier();
            That(fixture.Query("SELECT ordinal,claimed,coalesce(item_uid,0) FROM mailbox_attachments WHERE mail_id=$p0 ORDER BY ordinal", limited.MailId)
                is [[0L, 1L, 0L], [1L, 0L, long heldUid]] && heldUid == remainingUids[1],
                "第二只蛋仍托管在原邮件，不另建邮件、不扣走失败附件");
            string before = Snapshot();
            Extract(peer, [pending[1].ClaimId]); peer.Expect(1, 95, [0, 4, 0]); peer.Barrier();
            That(Snapshot() == before, "满包重试保持剩余领取资格、库存与审计不变");

            var pet = itemResources.Items.Get(63023);
            int gold = 1000 + pet.ShopPrice.GetSellGold(pet.Durability, pet.Durability);
            peer.Send(22, Build(w => { w.Write((byte)7); w.Write((short)139); w.Write(1); w.Write(1); w.Write(2); w.Write(295); }));
            peer.Expect(1, 22, Build(w => { w.Write((byte)1); w.Write(gold); w.Write((byte)7); w.Write((short)139); w.Write(1); }));
            if (gold != 1000) peer.Expect(0, 14);
            var empty = peer.Expect(0, 14).Body;
            That(empty[0] == 7 && U16(empty, 3) == 139 && U32(empty, 5) == uint.MaxValue, "正常出售腾出一个Pet槽位");
            peer.Send(96); peer.Expect(1, 96, [1, 0, 0]);
            var remaining = ReadList(peer.Expect(0, 97).Body, creatureItems: new HashSet<int> { 63022 }).Summaries
                .Single(row => row.MailId == limited.MailId);
            That(remaining.ClaimId == pending[1].ClaimId && U32(remaining.Core, 6) == remainingUids[1],
                "重开邮箱只出现原来未领的第二只蛋，领取ID与UID均不变");
            Extract(peer, [remaining.ClaimId]); ExpectClaim(peer, [remaining]);
            peer.Expect(0, 105);
            delta = peer.Expect(0, 14).Body;
            That(delta[0] == 7 && U16(delta, 3) == 139 && U32(delta, 9) == remainingUids[1], "空位恢复后可继续领取第二只蛋");
            peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM mailbox_attachments WHERE mail_id=$p0 AND claimed=1", limited.MailId) is [[2L]] &&
                fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=7 AND slot_index IN(0,139) ORDER BY slot_index", peer.Id)
                    is [[long first], [long second]] && first == remainingUids[0] && second == remainingUids[1],
                "最终收齐两个独立宠物实例，成功前缀不重复发放");
        }

        string Snapshot() => string.Join('\n', new[] { "characters", "character_inventory_items", "character_inventory_slots",
            "inventory_creature_details", "mailbox_messages", "mailbox_attachments", "mailbox_audit_log", "inventory_audit_log" }
            .Select(table => fixture.Snapshot("SELECT * FROM " + table + " ORDER BY 1")));
    }
}
