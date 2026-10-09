using static DfoServer.Testing.TestCheck;
using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;

namespace DfoServer;

internal static class MailboxPersistenceTests
{
    public static void Run()
    {
        int before = TestCheck.Count;
        Transfer();
        Rollback();
        ClaimDeadline();
        ExpirationAndStorage();
        DailyGold();
        Migration();
        Console.WriteLine($"PASS {TestCheck.Count - before} 邮箱 SQLite 归属、资产原子性与迁移断言");
    }

    static void Transfer()
    {
        using var f = new Fixture();
        var original = f.Sender.Inventory.Items;
        var sent = f.Send([original[0], original[2], original[3]], 1000);
        TestCheck.That(sent.Accepted && sent.MailId == 1 && sent.Character!.Inventory.Items.Length == 1, "整件三附件与寄送扣费一次提交");
        TestCheck.That(f.ReadSender().Revision == f.Sender.Revision && f.ReadReceiver().Inventory.Items.IsEmpty, "邮箱附件不出现在收件人实际背包");
        var page = f.Repository.LoadMailbox(f.ReceiverOwner, f.Receiver.Id, f.Now);
        TestCheck.That(page.Entries.Single().Attachments.Select(item => item.Item.Uid).SequenceEqual(new[] { original[0].Uid, original[2].Uid, original[3].Uid }),
            "邮箱保留装备、时装与宠物原 UID");
        TestCheck.That(page.Entries.Single().Attachments[1].Item.Avatar == original[2].Avatar &&
            page.Entries.Single().Attachments[2].Item.Creature == original[3].Creature && page.Entries.Single().Attachments[0].Item.Core == original[0].Core,
            "全核心、随机属性及独立详情无损");
        TestCheck.That(f.Repository.ChangeMailState(f.ReceiverOwner, f.Receiver.Id, [sent.MailId], 0, f.Now).Error == MailboxError.UnclaimedAssets,
            "未领取资产不可删除");
        var first = page.Entries.Single().Attachments[0];
        var claimed = f.Claim([first.ClaimId]);
        TestCheck.That(claimed.Accepted && f.Receiver.Inventory.Gold == 1000 && f.Receiver.Inventory.Items.Single().Uid == original[0].Uid,
            "逐附件领取同时领取一次金币并保留整件 UID");
        TestCheck.That(claimed.Claimed.SequenceEqual(new[] { new MailboxClaimChange(sent.MailId, first.ClaimId) }),
            "持久化领取返回实际邮件 key 和实际附件 key");
        string snapshot = f.Snapshot();
        TestCheck.That(f.Repository.LoadMailClaims(f.ReceiverOwner, f.Receiver.Id, [first.ClaimId], f.Now).IsEmpty && f.Snapshot() == snapshot,
            "重复领取无资格且完全只读");
        TestCheck.That(f.Repository.LoadMailClaims(f.OtherOwner, f.Other.Id, [page.Entries.Single().Attachments[1].ClaimId], f.Now).IsEmpty,
            "伪造另一角色附件 ID 无资格");
        var remaining = f.Repository.LoadMailbox(f.ReceiverOwner, f.Receiver.Id, f.Now).Entries.Single();
        TestCheck.That(remaining.Gold == 0 && remaining.Attachments.Length == 2 && remaining.State == 2, "金币清零、附件独立领取及已读状态共事务");
        f.Claim(remaining.Attachments.Select(item => item.ClaimId).ToImmutableArray());
        TestCheck.That(f.Receiver.Inventory.Items.Single(item => item.Avatar is not null).Avatar == original[2].Avatar &&
            f.Receiver.Inventory.Items.Single(item => item.Creature is not null).Creature == original[3].Creature,
            "批量领取后特殊详情仍按原 UID 保存");
        TestCheck.That(f.Repository.ChangeMailState(f.ReceiverOwner, f.Receiver.Id, [sent.MailId], 0, f.Now).Accepted,
            "领完后的正文可以删除");
        snapshot = f.Snapshot();
        TestCheck.That(f.Repository.ChangeMailState(f.ReceiverOwner, f.Receiver.Id, [sent.MailId], 0, f.Now).Accepted && f.Snapshot() == snapshot,
            "重复删除自己的邮件幂等且不再审计");

        var stack = f.Sender.Inventory.Items.Single();
        var partial = stack with { Uid = 0, Core = stack.Core with { Value = 4 } };
        f.Send([partial], 0);
        var splitMail = f.Repository.LoadMailbox(f.ReceiverOwner, f.Receiver.Id, f.Now).Entries.Single();
        var splitItem = splitMail.Attachments.Single().Item;
        TestCheck.That(splitItem.Uid != stack.Uid && splitItem.Uid > 0 && splitItem.Core == partial.Core && f.Sender.Inventory.Items.Single().Core.Value == 6,
            "部分堆叠在提交内分配新 UID，源余量与全属性保持");
        f.Claim([splitMail.Id]);
        TestCheck.That(f.Receiver.Inventory.Items.Single(item => item.Core.ItemId == stack.Core.ItemId).Uid == splitItem.Uid,
            "整封领取仍沿用分堆后的持久 UID");
        var reloaded = new CharacterRepository(f.Path);
        var expected = f.Receiver with { Inventory = f.Receiver.Inventory with { Items = f.Receiver.Inventory.Items
            .OrderBy(item => item.Position.List).ThenBy(item => item.Position.Index).ToImmutableArray() } };
        TestCheck.That(JsonSerializer.Serialize(reloaded.LoadCharacter(f.ReceiverOwner, 0)) == JsonSerializer.Serialize(expected),
            "仓储重开后角色、核心、详情与领取结果完全恢复");
        TestCheck.That(f.Query("SELECT count(*) FROM mailbox_attachments WHERE claimed=0") is 0L &&
            f.Query("SELECT count(*) FROM character_inventory_items i WHERE NOT EXISTS(SELECT 1 FROM character_inventory_slots s WHERE s.item_uid=i.item_uid) AND NOT EXISTS(SELECT 1 FROM mailbox_attachments m WHERE m.item_uid=i.item_uid)") is 0L,
            "领取结束无孤立或重复资产 owner");
        f.Integrity();
    }

    static void Rollback()
    {
        using var f = new Fixture();
        string before = f.Snapshot();
        f.Execute("CREATE TRIGGER fail_mail BEFORE INSERT ON mailbox_attachments BEGIN SELECT RAISE(ABORT,'mail-failure'); END");
        Reject<SqliteException>(() => f.Send([f.Sender.Inventory.Items[0]], 1000), "附件插入中途故障");
        TestCheck.That(f.Snapshot() == before, "寄送失败回滚金币、UID、源槽、revision、每日额度与全部审计");
        f.Execute("DROP TRIGGER fail_mail");
        var sent = f.Send([f.Sender.Inventory.Items[0]], 1000);
        var mail = f.Repository.LoadMailbox(f.ReceiverOwner, f.Receiver.Id, f.Now).Entries.Single();
        var plan = f.ClaimPlan([mail.Attachments.Single().ClaimId]);
        before = f.Snapshot();
        f.Execute("CREATE TRIGGER fail_claim BEFORE UPDATE OF state ON mailbox_messages BEGIN SELECT RAISE(ABORT,'claim-failure'); END");
        Reject<SqliteException>(() => f.Repository.ClaimMail(f.ReceiverOwner, f.Receiver, plan), "领取更新邮件状态时故障");
        TestCheck.That(f.Snapshot() == before && f.ReadReceiver().Inventory.Items.IsEmpty &&
            f.Repository.LoadMailClaims(f.ReceiverOwner, f.Receiver.Id, [mail.Attachments.Single().ClaimId], f.Now).Length == 1,
            "领取失败回滚背包、原 UID、金币、领取资格与审计");
        f.Execute("DROP TRIGGER fail_claim");
        f.Claim([mail.Attachments.Single().ClaimId]);
        var stale = f.Sender;
        f.Sender = f.Repository.CommitInventory(f.SenderOwner, f.Sender, new(f.Sender.Inventory, f.Sender.Inventory with { Gold = f.Sender.Inventory.Gold - 1 }, "other_mutation"));
        var stalePlan = f.SendPlan(stale, [], 1000);
        before = f.Snapshot();
        Reject<InvalidOperationException>(() => f.Repository.SendMail(f.SenderOwner, stale, stalePlan), "过期 revision 拒绝");
        TestCheck.That(f.Snapshot() == before, "过期规划不消耗每日额度或发信");
        using var next = f.Owners.Acquire(f.SenderOwner.Account, () => { });
        Reject<InvalidOperationException>(() => f.Repository.SendMail(f.SenderOwner, f.Sender, f.SendPlan(f.Sender, [], 0)), "旧 generation 不能写邮件");
        f.Integrity();
    }

    static void ClaimDeadline()
    {
        using var f = new Fixture();
        var delivery = new MailboxDelivery("deadline", f.Repository.FindMailRecipient("Receiver")!, "系统", "限时附件", 0,
            [new(0, new(InventoryListType.Main, 9), new(50002, ItemKind.Equipment, 999999998) { ExpireTime = checked((int)f.Now - 1) })], f.Now - 10);
        var mail = f.Repository.DeliverMail(delivery);
        var plan = f.ClaimPlan([mail.MailId]) with { Now = f.Now - 2 };
        string before = f.Snapshot();
        TestCheck.That(!plan.Items.Single().IsExpired(plan.Now) && plan.Items.Single().IsExpired(f.Now),
            "附件在原规划时有效，提交前已到期");
        TestCheck.That(f.Repository.ClaimMail(f.ReceiverOwner, f.Receiver, plan).Error == MailboxError.ExpiredItem && f.Snapshot() == before,
            "writer 等待跨越附件期限时拒绝，保留托管 UID、资格和全部资产");
        var cargo = f.Repository.DeliverMail(delivery with { Key = "cargo-prototype", Items =
            [new(0, new(InventoryListType.AccountCargo, 0), new(50002, ItemKind.Equipment, 999999998))] });
        long uid = f.Repository.LoadMailClaims(f.ReceiverOwner, f.Receiver.Id, [cargo.MailId], f.Now).Single().Mail.Attachments.Single().Item.Uid;
        TestCheck.That(f.Claim([cargo.MailId]).Accepted && f.ReadReceiver().Inventory.Items.Single().Uid == uid,
            "系统附件的原槽提示不改变托管角色归属，领取保留 UID");
        f.Integrity();
    }

    static void ExpirationAndStorage()
    {
        using var f = new Fixture();
        var recipient = f.Repository.FindMailRecipient("Receiver")!;
        var delivery = new MailboxDelivery("campaign:one", recipient, "系统", "永久奖励", 5,
            [new(0, new(InventoryListType.Main, 9), new(50002, ItemKind.Equipment, 999999998) { Rune = 22 })], f.Now);
        var sent = f.Repository.DeliverMail(delivery);
        string before = f.Snapshot();
        TestCheck.That(f.Repository.DeliverMail(delivery with { Created = f.Now + 10 }).MailId == sent.MailId && f.Snapshot() == before,
            "稳定系统投递 key 重试不新建邮件或附件");
        Reject<InvalidOperationException>(() => f.Repository.DeliverMail(delivery with { Gold = 6 }), "同一系统 key 不能改变资产");
        TestCheck.That(f.Snapshot() == before, "系统 key 冲突不写库");
        var expires = f.Repository.DeliverMail(delivery with { Key = "campaign:expire", Created = f.Now - 100, Expires = f.Now - 1 });
        TestCheck.That(f.Repository.LoadMailClaims(f.ReceiverOwner, f.Receiver.Id, [expires.MailId], f.Now).IsEmpty, "过期邮件不能领取");
        f.Repository.ExpireMailbox(f.ReceiverOwner, f.Receiver.Id, f.Now);
        TestCheck.That(f.Query($"SELECT state FROM mailbox_messages WHERE mail_id={expires.MailId}") is 0L &&
            f.Query("SELECT count(*) FROM mailbox_attachments WHERE claimed=0") is 1L, "过期清理只消耗到期资产，永久邮件保留");
        before = f.Snapshot();
        f.Repository.ExpireMailbox(f.ReceiverOwner, f.Receiver.Id, f.Now);
        TestCheck.That(f.Snapshot() == before, "重复到期清理零写入");

        var saved = ImmutableArray.CreateBuilder<uint>();
        for (int index = 0; index < 11; index++)
        {
            var row = f.Repository.DeliverMail(delivery with { Key = "saved:" + index, Gold = 0, Items = [], Created = f.Now + index });
            if (index < 10) saved.Add(row.MailId);
            else
                TestCheck.That(f.Repository.ChangeMailState(f.ReceiverOwner, f.Receiver.Id, saved.ToImmutable(), 3, f.Now).Accepted &&
                    f.Repository.ChangeMailState(f.ReceiverOwner, f.Receiver.Id, [row.MailId], 3, f.Now).Error == MailboxError.StorageFull,
                    "批量保存原子占用十封上限，超额拒绝");
        }
        before = f.Snapshot();
        var readSaved = f.Repository.ChangeMailState(f.ReceiverOwner, f.Receiver.Id, saved.ToImmutable(), 2, f.Now);
        TestCheck.That(readSaved.Accepted && readSaved.Changes.All(change => change.State == 3) && f.Snapshot() == before,
            "读取已保存邮件不降低保存状态，不申请 writer");
        using (var writer = f.Open())
        using (var tx = writer.BeginTransaction(deferred: false))
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            TestCheck.That(f.Repository.LoadMailbox(f.ReceiverOwner, f.Receiver.Id, f.Now).Entries.Count(mail => mail.State == 3) == 10 &&
                f.Repository.ChangeMailState(f.ReceiverOwner, f.Receiver.Id, saved.ToImmutable(), 2, f.Now).Accepted,
                "外部 writer 占用时，查询及已读零变化继续完成");
            TestCheck.That(watch.Elapsed < TimeSpan.FromSeconds(2), "只读邮箱没有等待 SQLite writer 的五秒超时");
        }
        for (int index = 0; index < 25; index++)
            f.Repository.DeliverMail(delivery with { Key = "page:" + index, Gold = 0, Items = [], Created = f.Now + 100 + index });
        var page = f.Repository.LoadMailbox(f.ReceiverOwner, f.Receiver.Id, f.Now);
        TestCheck.That(page.Entries.Length == 30 && page.NotLoaded == 7 && page.Entries.Count(mail => mail.State == 3) == 10,
            "最早二十封普通邮件与十封保存邮件完整分页，未装载数排除保存邮件");
        MailboxProtocolTests.CheckList(Network.Builders.MailboxPackets.List(page, f.Now), page.Entries, page.NotLoaded, f.Now);
        f.Integrity();
    }

    static void DailyGold()
    {
        using var f = new Fixture();
        f.Execute("UPDATE characters SET level=1 WHERE name='Sender'");
        f.Sender = f.ReadSender();
        var first = f.Send([], 10_000);
        TestCheck.That(first.Accepted && f.Query("SELECT amount FROM mailbox_sent_gold WHERE character_id=1") is 10_000L,
            "每日恰好达到上限的寄金币正常提交");
        string before = f.Snapshot();
        TestCheck.That(f.Send([], 1).Error == MailboxError.DailyGoldLimit && f.Snapshot() == before,
            "超过每日上限不扣手续费、金币或累计额度");
        var tomorrow = f.SendPlan(f.Sender, [], 1) with { Day = MailboxService.Day(DateTimeOffset.UtcNow.AddDays(1)) };
        var next = f.Repository.SendMail(f.SenderOwner, f.Sender, tomorrow);
        TestCheck.That(next.Accepted && f.Query("SELECT amount FROM mailbox_sent_gold WHERE character_id=1") is 1L &&
            f.Query("SELECT count(*) FROM mailbox_sent_gold WHERE character_id=1") is 1L, "跨日期原子重置单行每日累计数");
        f.Integrity();
    }

    static void Migration()
    {
        using var f = new Fixture();
        string character = JsonSerializer.Serialize(f.ReadSender() with { DungeonState = null });
        const string dropSettings = "DROP TABLE character_quest_completion_receipts; DROP TABLE character_quest_clear_receipts; DROP INDEX character_active_quest_acceptance; ALTER TABLE character_active_main_quests DROP COLUMN acceptance_id; DROP TABLE character_tp_sources; DROP TABLE character_skill_commands; DROP TABLE character_skill_combos; DROP TABLE character_skill_point_sources; DROP TABLE account_expert_contracts; DROP TABLE character_dungeon_settlement_receipts; DROP TABLE character_dungeon_pickup_receipts; DROP TABLE character_dungeon_loot_receipts; DROP TABLE character_dungeon_state; DROP TABLE character_game_settings; DROP TABLE account_client_settings; DROP TABLE character_client_settings;";
        const string dropMail = dropSettings + "DROP TABLE mailbox_audit_log; DROP TABLE mailbox_attachments; DROP TABLE mailbox_sent_gold; DROP TABLE mailbox_messages;";
        DatabaseSchemaTests.RewindQuestSystemToV13(f.Path);
        f.Execute(dropMail + "DROP TABLE character_dungeon_growth_receipts; ALTER TABLE account_wallets DROP COLUMN rental_points; PRAGMA user_version=1;");
        var upgraded = new CharacterRepository(f.Path);
        TestCheck.That(Convert.ToInt64(f.Query("PRAGMA user_version")) == SqliteMigrations.CurrentVersion &&
            JsonSerializer.Serialize(upgraded.LoadCharacter(f.SenderOwner, 0) with { DungeonState = null }) == character,
            "v1升级完整结构保留全部原角色、库存、UID及详情");
        f.Execute("INSERT INTO account_wallets(account,rental_points) VALUES('sender',123);");
        character = JsonSerializer.Serialize(f.ReadSender() with { DungeonState = null });
        DatabaseSchemaTests.RewindQuestSystemToV13(f.Path);
        f.Execute(dropMail + "DROP TABLE character_dungeon_growth_receipts; PRAGMA user_version=2;");
        upgraded = new CharacterRepository(f.Path);
        TestCheck.That(JsonSerializer.Serialize(upgraded.LoadCharacter(f.SenderOwner, 0) with { DungeonState = null }) == character &&
            f.Query("SELECT rental_points FROM account_wallets WHERE account='sender'") is 123L,
            "master v2升级保留租赁点数及原资产");
        DatabaseSchemaTests.RewindQuestSystemToV13(f.Path);
        f.Execute(dropMail + "ALTER TABLE character_dungeon_growth_receipts RENAME COLUMN resource_source TO resource_sha256; PRAGMA user_version=3;");
        f.Execute("""
            INSERT INTO character_dungeon_growth_receipts VALUES(
                1,'sender','legacy-event','legacy-instance',1,1,1,76125,20,1,1,1,
                'legacy-rule','legacy-resource-marker',1,1,95,95,0,1,1);
            """);
        upgraded = new CharacterRepository(f.Path);
        TestCheck.That(JsonSerializer.Serialize(upgraded.LoadCharacter(f.SenderOwner, 0) with { DungeonState = null }) == character &&
            f.Query("SELECT resource_source FROM character_dungeon_growth_receipts") is "legacy-resource-marker",
            "master v3升级保留成长账本历史来源值和全部资产");
        string before = f.Snapshot();
        _ = new CharacterRepository(f.Path);
        TestCheck.That(f.Snapshot() == before, "重开当前版本不重复迁移或赠送");
        DatabaseSchemaTests.RewindQuestSystemToV13(f.Path);
        f.Execute(dropMail + "ALTER TABLE character_dungeon_growth_receipts RENAME COLUMN resource_source TO resource_sha256; PRAGMA user_version=3; CREATE TABLE mailbox_attachments(collision INTEGER);");
        before = f.Snapshot();
        Reject<SqliteException>(() => new CharacterRepository(f.Path), "迁移中途 DDL 冲突");
        TestCheck.That(f.Query("PRAGMA user_version") is 3L && f.Snapshot() == before &&
            f.Query("SELECT count(*) FROM sqlite_schema WHERE name='mailbox_messages'") is 0L &&
            f.Query("SELECT count(*) FROM sqlite_schema WHERE name='mailbox_inbox'") is 0L, "升级失败回滚版本、资产、前序表和索引");
        f.Execute("DROP TABLE mailbox_attachments;");
        _ = new CharacterRepository(f.Path);
        f.Sender = f.ReadSender();
        f.Send([f.Sender.Inventory.Items[0]], 100);
        before = f.Snapshot();
        DatabaseSchemaTests.RewindQuestSystemToV13(f.Path);
        f.Execute(dropSettings + "ALTER TABLE character_dungeon_growth_receipts RENAME COLUMN resource_source TO resource_sha256; PRAGMA user_version=4;");
        _ = new CharacterRepository(f.Path);
        TestCheck.That(f.Snapshot() == before, "v4来源列迁移保留邮件、托管UID、金币、租赁点数及成长账本");
        f.Integrity();
        f.Execute($"PRAGMA user_version={SqliteMigrations.CurrentVersion + 1};");
        Reject<InvalidDataException>(() => new CharacterRepository(f.Path), "较新邮箱版本明确拒绝");
        TestCheck.That(Convert.ToInt64(f.Query("PRAGMA user_version")) == SqliteMigrations.CurrentVersion + 1, "不回退或覆盖较新数据库");
    }

    internal sealed class Fixture : IDisposable
    {
        readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "S5A9-mail-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(root, "game.db");
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease SenderOwner { get; }
        public AccountOwners.Lease ReceiverOwner { get; }
        public AccountOwners.Lease OtherOwner { get; }
        public CharacterRepository Repository { get; }
        public Character Sender { get; set; }
        public Character Receiver { get; set; }
        public Character Other { get; }
        public long Now { get; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public Fixture()
        {
            SenderOwner = Owners.Acquire("sender", () => { });
            ReceiverOwner = Owners.Acquire("receiver", () => { });
            OtherOwner = Owners.Acquire("other", () => { });
            Repository = new(Path);
            Sender = Repository.Create(SenderOwner, new("Sender", new(38, 1, 450, 234, 0), null) { Level = 95,
                Inventory = new([
                    new(0, new(InventoryListType.Main, 9), new(50002, ItemKind.Equipment, 999999998) { Rune = 4, Random0 = new(1, 2, 3),
                        RandomChangedIndex = 255, Extension97 = 123, GuardianGemKeys = 456, EmblemSocketCount = 1, Emblem1 = 999 }),
                    new(0, new(InventoryListType.Main, 121), new(50001, ItemKind.Material, 10) { EnchantCardId = 99, Extension121 = 42 }),
                    new(0, new(InventoryListType.Avatar, 0), new(50003, ItemKind.Avatar, 0)) { Avatar = new() { Color1 = 3, Color2 = 4, ClearAvatarId = 50005, Socket0 = new(2, 777) } },
                    new(0, new(InventoryListType.Pet, 0), new(50004, ItemKind.Creature, 0)) { Creature = new() { Name = "宠物", Mode = 1, Mode1Field0 = 2, Mode1Field1 = 3, Experience = 222, Level = 9, Satiety = 54 } }
                ], Gold: 500_000) });
            Receiver = Repository.Create(ReceiverOwner, new("Receiver", new(38, 1, 450, 234, 0), null) { Level = 95, Inventory = new([]) });
            Other = Repository.Create(OtherOwner, new("Other", new(38, 1, 450, 234, 0), null) { Inventory = new([]) });
        }
        public Character ReadSender() => Repository.LoadCharacter(SenderOwner, 0);
        public Character ReadReceiver() => Repository.LoadCharacter(ReceiverOwner, 0);
        public MailboxSendPlan SendPlan(Character sender, ImmutableArray<InventoryItem> attachments, uint gold)
        {
            var after = sender.Inventory;
            foreach (var item in attachments)
            {
                var source = after.Find(item.Position)!;
                after = after with { Items = item.Core.Count == source.Core.Count ? after.Items.Remove(source) :
                    after.Items.Replace(source, source with { Core = source.Core with { Value = source.Core.Count - item.Core.Count } }) };
            }
            uint fee = checked((uint)MailboxService.Fee(gold, attachments.Length));
            after = after with { Gold = after.Gold - gold - fee };
            return new(Repository.FindMailRecipient("Receiver")!, new("Receiver", gold, [], "正文"),
                new(sender.Inventory, after, "mail_send", new(sender)), attachments, fee, MailboxService.Day(DateTimeOffset.UtcNow), Now);
        }
        public MailboxResult Send(ImmutableArray<InventoryItem> attachments, uint gold)
        {
            var result = Repository.SendMail(SenderOwner, Sender, SendPlan(Sender, attachments, gold));
            if (result.Character is not null) Sender = result.Character;
            return result;
        }
        public MailboxClaimPlan ClaimPlan(ImmutableArray<uint> ids)
        {
            var claims = Repository.LoadMailClaims(ReceiverOwner, Receiver.Id, ids, Now);
            var items = claims.SelectMany(claim => claim.Attachment is null ? claim.Mail.Attachments : [claim.Attachment]).Select(attachment => attachment.Item).ToImmutableArray();
            var after = Receiver.Inventory;
            foreach (var item in items)
            {
                var list = item.Core.Kind switch { ItemKind.Avatar => InventoryListType.Avatar, ItemKind.Creature => InventoryListType.Pet, _ => InventoryListType.Main };
                short slot = list != InventoryListType.Main ? (short)0 : item.Core.IsStackable ? (short)121 : (short)9;
                while (after.Find(new(list, slot)) is not null) slot++;
                after = after with { Items = after.Items.Add(item with { Position = new(list, slot) }) };
            }
            var goldMails = claims.GroupBy(claim => claim.Mail.Id).Where(group => group.First().Mail.Gold > 0 &&
                group.Any(claim => claim.Attachment is null || claim.Attachment.Id == claim.Mail.Attachments[0].Id))
                .Select(group => group.First().Mail).ToArray();
            after = after with { Gold = checked(after.Gold + (uint)goldMails.Sum(mail => mail.Gold)) };
            return new(new(Receiver.Inventory, after, "mail_claim", new(Receiver)), claims, items, goldMails.Select(mail => mail.Id).ToImmutableArray(), Now);
        }
        public MailboxResult Claim(ImmutableArray<uint> ids)
        {
            var result = Repository.ClaimMail(ReceiverOwner, Receiver, ClaimPlan(ids));
            if (result.Character is not null) Receiver = result.Character;
            return result;
        }
        public SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open(); return db;
        }
        public void Execute(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public object? Query(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
        public string Snapshot()
        {
            using var db = Open();
            using var command = db.CreateCommand();
            var tables = new List<string>();
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using (var reader = command.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));
            var rows = new List<object>();
            foreach (string table in tables)
            {
                command.CommandText = "SELECT * FROM [" + table + "] ORDER BY rowid";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    rows.Add(new { Table = table, Values = Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue).ToArray() });
            }
            return JsonSerializer.Serialize(rows);
        }
        public void Integrity() => TestCheck.That(Query("PRAGMA integrity_check") is "ok" && Query("SELECT count(*) FROM pragma_foreign_key_check") is 0L, "邮件资产与数据库外键完整");
        public void Dispose() { OtherOwner.Dispose(); ReceiverOwner.Dispose(); SenderOwner.Dispose(); Directory.Delete(root, true); }
    }
}
