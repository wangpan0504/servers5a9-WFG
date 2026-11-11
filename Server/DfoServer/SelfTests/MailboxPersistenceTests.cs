using static DfoServer.Testing.TestCheck;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
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
        TalismanTransfer();
        Rollback();
        ClaimDeadline();
        OrdinaryPeriodIsolation();
        SendDeadline();
        ExpiredAttachmentDelete();
        ExpirationAndStorage();
        DailyGold();
        CharacterDeletion();
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

    static void TalismanTransfer()
    {
        using var f = new Fixture();
        const int stoneId = 100400002, runeId = 10605203;
        var stoneTemplate = new ItemTemplate(stoneId, ItemKind.Talisman, 34, 95, 0, 1, 0, 3, 97, "[free]", 1, 0, "stone");
        var runeTemplate = new ItemTemplate(runeId, ItemKind.Rune, 0, 95, 0, 999, 0, 2, 1, "[free]", 1, 0, "rune");
        var catalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        catalog.PrefillTemplates(new Dictionary<int, ItemTemplate> { [stoneId] = stoneTemplate, [runeId] = runeTemplate });
        catalog.PrefillTalismans([new(stoneTemplate, [1], 9)]);
        catalog.PrefillRunes([new(runeTemplate, [1], 9, RuneCamp.Terracotta, 2)]);
        var limits = new GoldLimitCatalog("[gold limit from level]\n0 1000000000\n[/gold limit from level]");
        var pieces = new EpicPieceCatalog("[piece list]\n50000 50001\n[/piece list]");
        var service = new MailboxService(f.Repository, catalog, limits, pieces);
        var stoneCore = new TalismanSocketColors(1, 2, 3).Apply(new(stoneId, ItemKind.Talisman, 999999998)) with
        { Extension97 = 123, Rune = 0x4321, Extension119 = 7 };
        var runeCore = new ItemCore(runeId, ItemKind.Rune, 5) { Extension97 = 456 };
        var inventory = new InventorySnapshot([
            new(0, new(InventoryListType.Talisman, 0), stoneCore),
            new(0, new(InventoryListType.Talisman, 70), runeCore)], Gold: 500_000);
        f.Sender = f.Repository.CommitInventory(f.SenderOwner, f.Sender, new(f.Sender.Inventory, inventory, "fixture_talisman"));
        var stone = f.Sender.Inventory.Find(new(InventoryListType.Talisman, 0))!;
        var rune = f.Sender.Inventory.Find(new(InventoryListType.Talisman, 70))!;
        var request = new MailboxSendRequest("Receiver", 0,
            [new(46, 0, stoneId, 1), new(46, 70, runeId, 2)], "talisman");
        var now = DateTimeOffset.FromUnixTimeSeconds(f.Now);
        That(service.PlanSend(f.Sender, f.SenderOwner.Account, request, now, out var sendPlan) == MailboxError.None &&
            sendPlan!.Attachments[0].Uid == stone.Uid && sendPlan.Attachments[1].Uid == 0 &&
            sendPlan.Inventory.After.Find(rune.Position)?.Core.Count == 3,
            "容器46寄送规划保留护石完整实例，符文分堆不重复原UID");
        var loadedRequest = request with { Items = [new(46, 210, runeId, 1)] };
        That(service.PlanSend(f.Sender, f.SenderOwner.Account, loadedRequest, now, out var rejected) == MailboxError.InvalidAttachment &&
            rejected is null, "邮件不能绕过专用卸载直接寄走已装符文");
        string before = f.Snapshot();
        f.Execute("CREATE TRIGGER reject_talisman_mail BEFORE INSERT ON mailbox_attachments BEGIN SELECT RAISE(ABORT,'fixture'); END");
        Reject<SqliteException>(() => service.Send(f.SenderOwner, f.Sender, sendPlan!), "护石邮件附件写入失败必须回滚");
        f.Execute("DROP TRIGGER reject_talisman_mail");
        That(f.Snapshot() == before, "护石邮件SQL失败不扣资产、金币或revision，不留邮件及审计");
        var sent = service.Send(f.SenderOwner, f.Sender, sendPlan!);
        That(sent.Accepted && sent.Character!.Inventory.Items is [{ Core.Count: 3 }] &&
            sent.Character.Inventory.Gold == 497_900, "护石与符文寄送同事务扣除实物和一次邮费");
        f.Sender = sent.Character!;
        var mail = f.Repository.LoadMailbox(f.ReceiverOwner, f.Receiver.Id, f.Now).Entries.Single();
        var ids = mail.Attachments.Select(item => item.ClaimId).ToImmutableArray();
        That(service.PlanClaim(f.ReceiverOwner, f.Receiver, ids, now, out var claimPlan) == MailboxError.None,
            "护石符文邮件通过正式领取规划");
        var claimed = service.Claim(f.ReceiverOwner, f.Receiver, claimPlan!);
        That(claimed.Accepted && claimed.Character!.Inventory.Find(stone.Position) is { } receivedStone &&
            receivedStone.Uid == stone.Uid && receivedStone.Core == stoneCore &&
            claimed.Character.Inventory.Find(rune.Position) is { } receivedRune &&
            receivedRune.Uid != rune.Uid && receivedRune.Core == runeCore with { Value = 2 } &&
            claimed.Character.Inventory.Items.All(item => item.Position.List == InventoryListType.Talisman),
            "邮件领取自动进入两种专用分页，完整核心与资产身份守恒");
        f.Receiver = claimed.Character!;
        That(JsonSerializer.Serialize(new CharacterRepository(f.Path).LoadCharacter(f.ReceiverOwner, 0)) == JsonSerializer.Serialize(f.Receiver),
            "容器46邮件领取重开库完整恢复，不回普通背包");
        f.Integrity();
    }

    static void CharacterDeletion()
    {
        using var f = new Fixture();
        var original = f.Sender.Inventory.Items;
        var sent = f.Send([original[0], original[2], original[3]], 1000);
        var page = f.Repository.LoadMailbox(f.ReceiverOwner, f.Receiver.Id, f.Now);
        var target = f.Repository.LoadDeletionTarget(f.SenderOwner, f.Sender.Slot);
        f.Repository.Delete(f.SenderOwner, target);
        TestCheck.That(JsonSerializer.Serialize(f.Repository.LoadMailbox(f.ReceiverOwner, f.Receiver.Id, f.Now)) == JsonSerializer.Serialize(page) &&
            f.Query("SELECT count(*) FROM mailbox_audit_log WHERE character_id=" + target.Id) is 1L,
            "删除发送者保留收件人的邮件、发件人快照、托管UID与寄送审计");
        var claim = f.Claim([sent.MailId]);
        TestCheck.That(claim.Accepted && f.Receiver.Inventory.Gold == 1000 &&
            f.Receiver.Inventory.Items.Select(item => item.Uid).SequenceEqual(new[] { original[0].Uid, original[2].Uid, original[3].Uid }),
            "发送者删除后仍可领取全部金币、原UID、装备和特殊详情");
        f.Repository.Delete(f.ReceiverOwner, f.Repository.LoadDeletionTarget(f.ReceiverOwner, f.Receiver.Slot));
        TestCheck.That(f.Query("SELECT count(*) FROM mailbox_messages") is 0L && f.Query("SELECT count(*) FROM mailbox_attachments") is 0L &&
            f.Query("SELECT count(*) FROM mailbox_audit_log") is 0L &&
            f.Query("SELECT count(*) FROM character_inventory_items WHERE character_id=" + f.Receiver.Id) is 0L,
            "删除收件者清理已领邮件历史、依赖审计和私人UID");
        f.Integrity();

        using var pending = new Fixture();
        pending.Send([pending.Sender.Inventory.Items[2], pending.Sender.Inventory.Items[3]], 0);
        pending.Repository.Delete(pending.ReceiverOwner, pending.Repository.LoadDeletionTarget(pending.ReceiverOwner, pending.Receiver.Slot));
        TestCheck.That(pending.Query("SELECT count(*) FROM inventory_avatar_details") is 0L &&
            pending.Query("SELECT count(*) FROM inventory_creature_details") is 0L &&
            pending.Query("SELECT count(*) FROM character_inventory_items WHERE character_id=" + pending.Receiver.Id) is 0L,
            "删除收件者清理未领时装、宠物托管UID与级联详情，不残留孤立附件");
        pending.Integrity();
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

    static void OrdinaryPeriodIsolation()
    {
        using var f = new Fixture();
        var equipment = f.Sender.Inventory.Find(new(InventoryListType.Main, 9))!;
        var avatar = f.Sender.Inventory.Find(new(InventoryListType.Avatar, 0))!;
        var pet = f.Sender.Inventory.Find(new(InventoryListType.Pet, 0))!;
        int expire = checked((int)(f.Now + 86400));
        var periodCore = equipment.Core with { ExpireTime = expire };
        var inventory = f.Sender.Inventory with { Items = f.Sender.Inventory.Items
            .Replace(equipment, equipment with { Core = periodCore })
            .Add(new(0, new(InventoryListType.Main, 10), periodCore)) };
        f.Sender = f.Repository.CommitInventory(f.SenderOwner, f.Sender, new(f.Sender.Inventory, inventory, "period_pair"));
        var live = f.Sender.Inventory.Find(new(InventoryListType.Main, 9))!;
        var later = f.Sender.Inventory.Find(new(InventoryListType.Main, 10))!;
        That(live.Uid == equipment.Uid && later.Uid > 0 && later.Uid != live.Uid && live.ExpireTime == expire && later.ExpireTime == expire,
            "两件普通期限实例各自持有截止，且第二件分配了新 UID");
        var catalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        catalog.PrefillTemplates(new Dictionary<int, ItemTemplate>
        {
            [50002] = new(50002, ItemKind.Equipment, 12, 1, 0, 1, 0, 0, 1, "[free]", 65535, 0, "期限") { Expiration = new(Days: 30) },
            [50003] = new(50003, ItemKind.Avatar, 0, 1, 0, 1, 0, 0, 1, "[free]", 65535, 0, "时装"),
            [50004] = new(50004, ItemKind.Creature, 0, 1, 0, 1, 0, 0, 1, "[free]", 65535, 0, "宠物")
        });
        var service = new MailboxService(f.Repository, catalog,
            new GoldLimitCatalog("[gold limit from level]\n0 1000000000\n[/gold limit from level]"),
            new EpicPieceCatalog("[piece list]\n50000 50001\n[/piece list]"));
        var now = DateTimeOffset.FromUnixTimeSeconds(f.Now);
        string before = f.Snapshot();
        MailboxError Reject(MailboxSendRequest request)
        {
            var error = service.PlanSend(f.Sender, f.SenderOwner.Account, request, now, out var plan);
            That(plan is null, "被拒绝的寄送没有计划");
            return error;
        }
        That(Reject(new("Receiver", 80, [new(0, 9, 50002, 1)], "金币")) == MailboxError.InvalidAttachment &&
            Reject(new("Receiver", 0, [new(0, 9, 50002, 1), new(1, 0, 50003, 1)], "附件")) == MailboxError.InvalidAttachment &&
            Reject(new("Receiver", 0, [new(0, 9, 50002, 1), new(0, 10, 50002, 1)], "两件期限")) == MailboxError.InvalidAttachment &&
            f.Snapshot() == before,
            "普通期限不能与金币或其他附件同封，拒绝不扣资产也不写邮件");
        var ordinary = new MailboxSendRequest("Receiver", 80, [new(1, 0, 50003, 1), new(3, 0, 50004, 1)], "金币和附件");
        That(service.PlanSend(f.Sender, f.SenderOwner.Account, ordinary, now, out var ordinaryPlan) == MailboxError.None && ordinaryPlan is not null,
            "没有期限附件时，金币和多附件仍可同封");
        var sentOrdinary = service.Send(f.SenderOwner, f.Sender, ordinaryPlan!);
        That(sentOrdinary.Accepted, "金币和多附件寄送提交成功");
        f.Sender = sentOrdinary.Character!;
        That(service.PlanSend(f.Sender, f.SenderOwner.Account, new("Receiver", 0, [new(0, 9, 50002, 1)], "未到期"), now, out var livePlan) == MailboxError.None,
            "单独的未到期普通期限可以寄送");
        var sentLive = service.Send(f.SenderOwner, f.Sender, livePlan!);
        That(sentLive.Accepted, "第一件期限附件寄送成功");
        f.Sender = sentLive.Character!;
        That(service.PlanSend(f.Sender, f.SenderOwner.Account, new("Receiver", 0, [new(0, 10, 50002, 1)], "稍后到期"), now, out var laterPlan) == MailboxError.None,
            "另一件单独的普通期限也可以寄送");
        var sentLater = service.Send(f.SenderOwner, f.Sender, laterPlan!);
        That(sentLater.Accepted && sentLater.Character!.Inventory.Gold == 495_616, "第二件期限附件寄送成功，三次拒绝没有多扣金币");
        f.Sender = sentLater.Character!;
        var page = f.Repository.LoadMailbox(f.ReceiverOwner, f.Receiver.Id, f.Now);
        var goldMail = page.Entries.Single(mail => mail.Gold == 80);
        var liveMail = page.Entries.Single(mail => mail.Attachments.Any(item => item.Item.Uid == live.Uid));
        var laterMail = page.Entries.Single(mail => mail.Attachments.Any(item => item.Item.Uid == later.Uid));
        That(page.Entries.Length == 3 && goldMail.Attachments.Length == 2 && liveMail.Attachments.Length == 1 && laterMail.Attachments.Length == 1,
            "金币多附件与两件期限附件分成三封");
        string held = f.Snapshot();
        var expiredAt = DateTimeOffset.FromUnixTimeSeconds(expire);
        That(service.PlanClaim(f.ReceiverOwner, f.Receiver, [laterMail.Id], expiredAt, out var expiredPlan) == MailboxError.ExpiredItem && expiredPlan is null &&
            f.Snapshot() == held &&
            service.PlanClaim(f.ReceiverOwner, f.Receiver, [laterMail.Attachments.Single().ClaimId], expiredAt, out expiredPlan) == MailboxError.ExpiredItem &&
            expiredPlan is null && f.Snapshot() == held,
            "到期后整封和单附件领取都拒绝，不删除、不入包、不重复消费");
        That(service.PlanClaim(f.ReceiverOwner, f.Receiver, [goldMail.Id], now, out var goldPlan) == MailboxError.None, "过期期限邮件不挡住另一封金币和附件");
        var claimedGold = service.Claim(f.ReceiverOwner, f.Receiver, goldPlan!);
        That(claimedGold.Accepted, "金币和多附件领取提交成功");
        f.Receiver = claimedGold.Character!;
        That(f.Receiver.Inventory.Gold == 80 && f.Receiver.Inventory.Items.Single(item => item.Avatar is not null).Avatar == avatar.Avatar &&
            f.Receiver.Inventory.Items.Single(item => item.Creature is not null).Creature == pet.Creature &&
            service.PlanClaim(f.ReceiverOwner, f.Receiver, [goldMail.Id], now, out _) == MailboxError.MailMissing &&
            f.ReadReceiver().Inventory.Gold == 80,
            "金币只领取一次，时装和宠物详情保留，重复领取没有第二次入包");
        That(service.PlanClaim(f.ReceiverOwner, f.Receiver, [liveMail.Attachments.Single().ClaimId], now, out var liveClaim) == MailboxError.None,
            "同一件期限附件在到期前可以领取");
        var claimedLive = service.Claim(f.ReceiverOwner, f.Receiver, liveClaim!);
        That(claimedLive.Accepted, "到期前领取提交成功");
        f.Receiver = claimedLive.Character!;
        That(f.Receiver.Inventory.Items.Count(item => item.Uid == live.Uid) == 1 &&
            f.Receiver.Inventory.Items.Single(item => item.Uid == live.Uid).ExpireTime == expire &&
            service.PlanClaim(f.ReceiverOwner, f.Receiver, [liveMail.Attachments.Single().ClaimId], now, out _) == MailboxError.MailMissing &&
            f.ReadReceiver().Inventory.Items.Count(item => item.Uid == live.Uid) == 1,
            "到期前领取保留原 UID 和截止，重复领取不能再拿一份");
        That(service.PlanClaim(f.ReceiverOwner, f.Receiver, [laterMail.Id], expiredAt, out _) == MailboxError.ExpiredItem &&
            !f.ReadReceiver().Inventory.Items.Any(item => item.Uid == later.Uid) &&
            f.Query("SELECT count(*) FROM mailbox_attachments WHERE claimed=0") is 1L,
            "已领取的金币、附件和未到期券不影响另一封到期附件，它仍留在邮件里");
        f.Integrity();
    }

    static void SendDeadline()
    {
        var start = DateTimeOffset.UtcNow;
        var clock = new MutableClock(start);
        using var f = new Fixture(clock);
        int expire = checked((int)start.AddHours(1).ToUnixTimeSeconds());
        var equipment = f.Sender.Inventory.Find(new(InventoryListType.Main, 9))!;
        var periodCore = equipment.Core with { ExpireTime = expire };
        f.Sender = f.Repository.CommitInventory(f.SenderOwner, f.Sender, new(f.Sender.Inventory, f.Sender.Inventory with
        {
            Items = f.Sender.Inventory.Items.Replace(equipment, equipment with { Core = periodCore })
                .Add(new(0, new(InventoryListType.Main, 10), periodCore))
        }, "send_deadline"));
        var first = f.Sender.Inventory.Find(new(InventoryListType.Main, 9))!;
        var second = f.Sender.Inventory.Find(new(InventoryListType.Main, 10))!;
        var catalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        catalog.PrefillTemplates(new Dictionary<int, ItemTemplate>
        {
            [50002] = new(50002, ItemKind.Equipment, 12, 1, 0, 1, 0, 0, 1, "[free]", 65535, 0, "期限") { Expiration = new(Days: 30) }
        });
        var service = new MailboxService(f.Repository, catalog,
            new GoldLimitCatalog("[gold limit from level]\n0 1000000000\n[/gold limit from level]"),
            new EpicPieceCatalog("[piece list]\n50000 50001\n[/piece list]"));
        That(service.PlanSend(f.Sender, f.SenderOwner.Account, new("Receiver", 0, [new(0, 9, 50002, 1)], "未到期"), start, out var livePlan) == MailboxError.None &&
            livePlan is not null && livePlan.Attachments.Single().Uid == first.Uid && !first.IsExpired(start.ToUnixTimeSeconds()),
            "截止前的普通期限附件可以规划寄送");
        var planned = livePlan ?? throw new InvalidOperationException("截止前寄送没有计划");
        var sent = WhileWriterQueued(f.Path, null, () => f.Repository.SendMail(f.SenderOwner, f.Sender, planned));
        That(sent.Accepted && sent.Character is not null, "截止前排队寄送被接受");
        f.Sender = sent.Character ?? throw new InvalidOperationException("截止前排队寄送没有角色");
        var mailed = f.Repository.LoadMailbox(f.ReceiverOwner, f.Receiver.Id, start.ToUnixTimeSeconds()).Entries.Single();
        That(sent.MailId == mailed.Id && f.Sender.Inventory.Gold == planned.Inventory.After.Gold &&
            f.Sender.Inventory.Find(first.Position) is null && mailed.Attachments.Single().Item.Uid == first.Uid &&
            mailed.Attachments.Single().Item.ExpireTime == expire && f.ReadReceiver().Inventory.Items.IsEmpty &&
            f.Query("SELECT count(*) FROM mailbox_sent_gold") is 0L && f.Query("SELECT count(*) FROM mailbox_audit_log") is 1L,
            "writer 排队但尚未跨截止时扣一次邮费并托管原 UID，不把附件放进收件背包");
        That(service.PlanSend(f.Sender, f.SenderOwner.Account, new("Receiver", 0, [new(0, 10, 50002, 1)], "排队"), start, out var queuedPlan) == MailboxError.None &&
            queuedPlan is not null, "另一件期限附件在截止前仍可规划");
        var queued = queuedPlan ?? throw new InvalidOperationException("跨截止寄送没有计划");
        string before = f.Snapshot();
        var rejected = WhileWriterQueued(f.Path, () => clock.Set(start.AddHours(2)), () => f.Repository.SendMail(f.SenderOwner, f.Sender, queued));
        That(rejected.Error == MailboxError.ExpiredItem && rejected.Character is null && rejected.MailId == 0 && f.Snapshot() == before,
            "writer 排队跨过附件截止时返回过期，不扣邮费、不投递、不改双方资产");
        That(f.Repository.SendMail(f.SenderOwner, f.Sender, queued).Error == MailboxError.ExpiredItem && f.Snapshot() == before,
            "过期后重试同一计划仍然拒绝，且不再写库");
        clock.Set(start);
        var retried = f.Repository.SendMail(f.SenderOwner, f.Sender, queued);
        That(retried.Accepted && retried.Character is not null, "回到截止前的重试被接受");
        f.Sender = retried.Character ?? throw new InvalidOperationException("回到截止前的重试没有角色");
        var again = f.Repository.LoadMailbox(f.ReceiverOwner, f.Receiver.Id, start.ToUnixTimeSeconds()).Entries.Single(mail => mail.Id != mailed.Id);
        That(again.Attachments.Single().Item.Uid == second.Uid && again.Attachments.Single().Item.ExpireTime == expire &&
            f.Sender.Inventory.Gold == queued.Inventory.After.Gold && f.ReadSender().Inventory.Find(second.Position) is null &&
            f.Query("SELECT count(*) FROM mailbox_messages") is 2L && f.Query("SELECT count(*) FROM mailbox_audit_log") is 2L,
            "时钟回到截止前，同一计划可以重新寄送，只再扣一次邮费");
        f.Integrity();
    }

    static T WhileWriterQueued<T>(string path, Action? whileQueued, Func<T> action)
    {
        T result = default!;
        Exception? failure = null;
        using var finished = new ManualResetEventSlim();
        var worker = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception error) { failure = error; }
            finally { finished.Set(); }
        }) { IsBackground = true, Name = "mail-deadline-writer" };
        var gate = (SemaphoreSlim)typeof(SqliteWriteGate).GetField("Gate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var waiters = typeof(SemaphoreSlim).GetField("m_waitCount", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? typeof(SemaphoreSlim).GetField("_waitCount", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("无法观察原写入闸门等待计数");
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
        db.Open();
        bool started = false;
        try
        {
            var write = db.BeginWrite("MailboxPersistenceTests.DeadlineHold");
            try
            {
                var tx = write.Transaction;
                try
                {
                    worker.Start();
                    started = true;
                    That(SpinWait.SpinUntil(() => Convert.ToInt32(waiters.GetValue(gate)) > 0 || finished.IsSet, TimeSpan.FromSeconds(5)) &&
                        !finished.IsSet, "寄送实际等待原写入闸门");
                    whileQueued?.Invoke();
                    That(!finished.IsSet, "调整时钟时寄送仍堵在 writer 上");
                }
                finally { tx.Dispose(); }
            }
            finally { write.Dispose(); }
            if (started && (!finished.Wait(TimeSpan.FromSeconds(10)) || !worker.Join(TimeSpan.FromSeconds(5))))
                throw new TimeoutException("释放写入闸门后寄送未退出");
        }
        finally { db.Dispose(); }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        long ticks = start.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Set(DateTimeOffset value) => Interlocked.Exchange(ref ticks, value.UtcTicks);
    }

    static void ExpiredAttachmentDelete()
    {
        using var f = new Fixture();
        int expire = checked((int)(f.Now + 3600));
        var equipment = f.Sender.Inventory.Find(new(InventoryListType.Main, 9))!;
        var periodCore = equipment.Core with { ExpireTime = expire };
        var inventory = f.Sender.Inventory with { Items = f.Sender.Inventory.Items
            .Replace(equipment, equipment with { Core = periodCore })
            .Add(new(0, new(InventoryListType.Main, 10), periodCore)) };
        f.Sender = f.Repository.CommitInventory(f.SenderOwner, f.Sender, new(f.Sender.Inventory, inventory, "period_delete"));
        var normalItem = f.Sender.Inventory.Find(new(InventoryListType.Main, 9))!;
        var savedItem = f.Sender.Inventory.Find(new(InventoryListType.Main, 10))!;
        var sentNormal = f.Send([normalItem], 0);
        var sentSaved = f.Send([savedItem], 0);
        That(sentNormal.Accepted && sentSaved.Accepted && normalItem.Uid != savedItem.Uid, "普通邮件和准备保存的期限邮件都寄出");
        var recipient = f.Repository.FindMailRecipient("Receiver")!;
        var fillers = new List<uint>();
        for (int index = 0; index < 9; index++)
        {
            var row = f.Repository.DeliverMail(new("period-save:" + index, recipient, "系统", "期限", 0,
                [new(0, new(InventoryListType.Main, 9), new(50002, ItemKind.Equipment, 999999998) { ExpireTime = expire })], f.Now));
            That(row.Accepted, "保存栏填充邮件投递成功");
            fillers.Add(row.MailId);
        }
        var empty = f.Repository.DeliverMail(new("period-empty", recipient, "系统", "空位", 0, [], f.Now));
        var gold = f.Repository.DeliverMail(new("period-gold", recipient, "系统", "金币", 5,
            [new(0, new(InventoryListType.Main, 9), new(50002, ItemKind.Equipment, 999999998) { ExpireTime = expire })], f.Now));
        var mixed = f.Repository.DeliverMail(new("period-mixed", recipient, "系统", "混合", 0,
            [
                new(0, new(InventoryListType.Main, 9), new(50002, ItemKind.Equipment, 999999998) { ExpireTime = expire }),
                new(0, new(InventoryListType.Main, 10), new(50002, ItemKind.Equipment, 999999998))
            ], f.Now));
        That(empty.Accepted && gold.Accepted && mixed.Accepted, "空邮件、带金币的到期附件和混合附件都投递成功");
        var savedIds = fillers.ToImmutableArray().Add(sentSaved.MailId);
        That(f.Repository.ChangeMailState(f.ReceiverOwner, f.Receiver.Id, savedIds, 3, f.Now).Accepted, "十封带期限附件的邮件占满保存栏");
        That(f.Repository.ChangeMailState(f.ReceiverOwner, f.Receiver.Id, [empty.MailId], 3, f.Now).Error == MailboxError.StorageFull,
            "保存栏已满时再保存被拒绝");
        string before = f.Snapshot();
        That(f.Repository.ChangeMailState(f.ReceiverOwner, f.Receiver.Id, [sentNormal.MailId], 0, f.Now).Error == MailboxError.UnclaimedAssets &&
            f.Repository.ChangeMailState(f.ReceiverOwner, f.Receiver.Id, [sentSaved.MailId], 0, f.Now).Error == MailboxError.UnclaimedAssets &&
            f.Repository.ChangeMailState(f.ReceiverOwner, f.Receiver.Id, [fillers[0]], 0, f.Now).Error == MailboxError.UnclaimedAssets &&
            f.Snapshot() == before, "到期前普通邮件和保存邮件都不能删除，库保持原样");
        That(f.Repository.LoadMailClaims(f.ReceiverOwner, f.Receiver.Id, [sentNormal.MailId], f.Now).Length == 1 &&
            f.Repository.LoadMailbox(f.ReceiverOwner, f.Receiver.Id, f.Now).Entries.Count(mail => mail.State == 3) == 10,
            "到期前附件仍可领取，十个保存位仍被占用");

        long at = expire;
        var service = new MailboxService(f.Repository,
            new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0),
            new GoldLimitCatalog("[gold limit from level]\n0 1000000000\n[/gold limit from level]"),
            new EpicPieceCatalog("[piece list]\n50000 50001\n[/piece list]"));
        var expiredAt = DateTimeOffset.FromUnixTimeSeconds(at);
        string held = f.Snapshot();
        That(service.PlanClaim(f.ReceiverOwner, f.Receiver, [sentNormal.MailId], expiredAt, out var expiredPlan) == MailboxError.ExpiredItem &&
            expiredPlan is null &&
            service.PlanClaim(f.ReceiverOwner, f.Receiver, [sentSaved.MailId], expiredAt, out expiredPlan) == MailboxError.ExpiredItem &&
            expiredPlan is null && f.Snapshot() == held &&
            !f.ReadReceiver().Inventory.Items.Any(item => item.Uid == normalItem.Uid || item.Uid == savedItem.Uid),
            "到期后领取仍拒绝，不入包也不删除");
        That(f.Repository.ChangeMailState(f.ReceiverOwner, f.Receiver.Id, [gold.MailId], 0, at).Error == MailboxError.UnclaimedAssets &&
            f.Query($"SELECT gold FROM mailbox_messages WHERE mail_id={gold.MailId}") is 5L &&
            f.Query($"SELECT count(*) FROM mailbox_attachments WHERE mail_id={gold.MailId} AND claimed=0") is 1L,
            "未领金币即使附件已到期也拒绝删除");
        That(f.Repository.ChangeMailState(f.ReceiverOwner, f.Receiver.Id, [mixed.MailId], 0, at).Error == MailboxError.UnclaimedAssets &&
            f.Query($"SELECT count(*) FROM mailbox_attachments WHERE mail_id={mixed.MailId} AND claimed=0") is 2L,
            "同一封还有未到期附件时整封拒绝，不部分作废");
        That(f.Repository.ChangeMailState(f.ReceiverOwner, f.Receiver.Id, [sentNormal.MailId, gold.MailId], 0, at).Error == MailboxError.UnclaimedAssets &&
            f.Query($"SELECT state FROM mailbox_messages WHERE mail_id={sentNormal.MailId}") is 1L &&
            f.Query($"SELECT count(*) FROM mailbox_attachments WHERE mail_id={sentNormal.MailId} AND claimed=0") is 1L,
            "同一批里有受保护邮件时，不删除另一封已到期邮件");

        held = f.Snapshot();
        f.Execute("CREATE TRIGGER reject_void_expired BEFORE DELETE ON character_inventory_items BEGIN SELECT RAISE(ABORT,'void'); END");
        Reject<SqliteException>(() => f.Repository.ChangeMailState(f.ReceiverOwner, f.Receiver.Id, [sentNormal.MailId], 0, at), "作废托管实例失败");
        f.Execute("DROP TRIGGER reject_void_expired");
        That(f.Snapshot() == held, "作废失败回滚邮件状态、附件资格和托管 UID");

        var deletedNormal = f.Repository.ChangeMailState(f.ReceiverOwner, f.Receiver.Id, [sentNormal.MailId], 0, at);
        That(deletedNormal.Accepted && deletedNormal.Changes.Single().State == 0, "到期后普通邮件可以删除");
        That(f.Query($"SELECT state FROM mailbox_messages WHERE mail_id={sentNormal.MailId}") is 0L &&
            f.Query($"SELECT count(*) FROM mailbox_attachments WHERE mail_id={sentNormal.MailId} AND claimed=1 AND item_uid IS NULL") is 1L &&
            f.Query($"SELECT count(*) FROM character_inventory_items WHERE item_uid={normalItem.Uid}") is 0L &&
            f.Query($"SELECT assets FROM mailbox_audit_log WHERE mail_id={sentNormal.MailId} AND action_name='state'") is string assets &&
            assets.Contains(normalItem.Uid.ToString(), StringComparison.Ordinal) &&
            !f.ReadReceiver().Inventory.Items.Any(item => item.Uid == normalItem.Uid) &&
            !f.ReadSender().Inventory.Items.Any(item => item.Uid == normalItem.Uid) &&
            service.PlanClaim(f.ReceiverOwner, f.Receiver, [sentNormal.MailId], expiredAt, out _) == MailboxError.MailMissing,
            "普通邮件的到期附件随删除作废，不入包且不能再领");
        string once = f.Snapshot();
        That(f.Repository.ChangeMailState(f.ReceiverOwner, f.Receiver.Id, [sentNormal.MailId], 0, at).Accepted && f.Snapshot() == once,
            "重复删除普通邮件幂等且不再写库");
        That(f.Repository.LoadMailbox(f.ReceiverOwner, f.Receiver.Id, at).Entries.Count(mail => mail.State == 3) == 10,
            "删除未保存邮件不释放保存位");
        var deletedSaved = f.Repository.ChangeMailState(f.ReceiverOwner, f.Receiver.Id, [sentSaved.MailId], 0, at);
        That(deletedSaved.Accepted &&
            f.Query($"SELECT state FROM mailbox_messages WHERE mail_id={sentSaved.MailId}") is 0L &&
            f.Query($"SELECT count(*) FROM character_inventory_items WHERE item_uid={savedItem.Uid}") is 0L &&
            !f.ReadReceiver().Inventory.Items.Any(item => item.Uid == savedItem.Uid) &&
            f.Repository.LoadMailbox(f.ReceiverOwner, f.Receiver.Id, at).Entries.Count(mail => mail.State == 3) == 9,
            "到期后保存邮件删除成功并让出一个保存位");
        That(f.Repository.ChangeMailState(f.ReceiverOwner, f.Receiver.Id, [empty.MailId], 3, at).Accepted &&
            f.Repository.LoadMailbox(f.ReceiverOwner, f.Receiver.Id, at).Entries.Count(mail => mail.State == 3) == 10,
            "释放出的保存位可以保存另一封邮件");
        once = f.Snapshot();
        That(f.Repository.ChangeMailState(f.ReceiverOwner, f.Receiver.Id, [sentSaved.MailId], 0, at).Accepted && f.Snapshot() == once,
            "重复删除保存邮件幂等且不再写库");

        var reloaded = new CharacterRepository(f.Path);
        var restoredPage = reloaded.LoadMailbox(f.ReceiverOwner, f.Receiver.Id, at);
        That(restoredPage.Entries.All(mail => mail.Id != sentNormal.MailId && mail.Id != sentSaved.MailId) &&
            restoredPage.Entries.Count(mail => mail.State == 3) == 10 &&
            restoredPage.Entries.Any(mail => mail.Id == empty.MailId && mail.State == 3) &&
            reloaded.LoadMailClaims(f.ReceiverOwner, f.Receiver.Id, [sentNormal.MailId, sentSaved.MailId], at).IsEmpty &&
            reloaded.LoadCharacter(f.ReceiverOwner, 0).Inventory.Items.All(item => item.Uid != normalItem.Uid && item.Uid != savedItem.Uid),
            "重启后两封已删邮件不可见、不可再领，保存位和空邮件保持");
        string restored = f.Snapshot();
        That(reloaded.ChangeMailState(f.ReceiverOwner, f.Receiver.Id, [sentNormal.MailId, sentSaved.MailId], 0, at).Accepted &&
            f.Snapshot() == restored, "重启后重复删除成功且不再写库");
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
        const string dropSettings = "DROP TABLE IF EXISTS character_dungeon_fatigue_receipts; DROP TABLE IF EXISTS character_fatigue; DROP TABLE character_quest_completion_receipts; DROP TABLE character_quest_clear_receipts; DROP INDEX character_active_quest_acceptance; ALTER TABLE character_active_main_quests DROP COLUMN acceptance_id; DROP TABLE character_tp_sources; DROP TABLE character_skill_commands; DROP TABLE character_skill_combos; DROP TABLE character_skill_point_sources; DROP TABLE account_expert_contracts; DROP TABLE character_dungeon_settlement_receipts; DROP TABLE character_dungeon_pickup_receipts; DROP TABLE character_dungeon_loot_receipts; DROP TABLE character_dungeon_state; DROP TABLE character_game_settings; DROP TABLE account_client_settings; DROP TABLE character_client_settings;";
        const string dropMail = dropSettings + "DROP TABLE mailbox_audit_log; DROP TABLE mailbox_attachments; DROP TABLE mailbox_sent_gold; DROP TABLE mailbox_messages;";
        DatabaseSchemaTests.RewindQuestSystemToV13(f.Path);
        DatabaseSchemaTests.RewindRaidLifecycle(f.Path);
        f.Execute(dropMail + "DROP TABLE character_dungeon_growth_receipts; ALTER TABLE account_wallets DROP COLUMN rental_points; PRAGMA user_version=1;");
        var upgraded = new CharacterRepository(f.Path);
        TestCheck.That(Convert.ToInt64(f.Query("PRAGMA user_version")) == SqliteMigrations.CurrentVersion &&
            JsonSerializer.Serialize(upgraded.LoadCharacter(f.SenderOwner, 0) with { DungeonState = null }) == character,
            "v1升级完整结构保留全部原角色、库存、UID及详情");
        f.Execute("INSERT INTO account_wallets(account,rental_points) VALUES('sender',123);");
        character = JsonSerializer.Serialize(f.ReadSender() with { DungeonState = null });
        DatabaseSchemaTests.RewindQuestSystemToV13(f.Path);
        DatabaseSchemaTests.RewindRaidLifecycle(f.Path);
        f.Execute(dropMail + "DROP TABLE character_dungeon_growth_receipts; PRAGMA user_version=2;");
        upgraded = new CharacterRepository(f.Path);
        TestCheck.That(JsonSerializer.Serialize(upgraded.LoadCharacter(f.SenderOwner, 0) with { DungeonState = null }) == character &&
            f.Query("SELECT rental_points FROM account_wallets WHERE account='sender'") is 123L,
            "master v2升级保留租赁点数及原资产");
        DatabaseSchemaTests.RewindQuestSystemToV13(f.Path);
        DatabaseSchemaTests.RewindRaidLifecycle(f.Path);
        f.Execute(dropMail + "ALTER TABLE character_dungeon_growth_receipts RENAME COLUMN resource_source TO resource_sha256; PRAGMA user_version=3;");
        f.Execute("""
            INSERT INTO character_dungeon_growth_receipts(character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,combat_sequence,generation,amount,rule_id,resource_sha256,monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision) VALUES(
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
        DatabaseSchemaTests.RewindRaidLifecycle(f.Path);
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
        DatabaseSchemaTests.RewindRaidLifecycle(f.Path);
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
        public Fixture(TimeProvider? clock = null)
        {
            SenderOwner = Owners.Acquire("sender", () => { });
            ReceiverOwner = Owners.Acquire("receiver", () => { });
            OtherOwner = Owners.Acquire("other", () => { });
            Repository = new(Path, promotionClock: clock);
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
                var list = ItemSlotBoundService.List(item.Core.Kind);
                short slot = ItemSlotBoundService.Range(item.Core.Kind, after).Start;
                while (after.Find(new(list, slot)) is not null) slot++;
                after = after with { Items = after.Items.Add(item with { Position = new(list, slot) }) };
            }
            var goldMails = claims.GroupBy(claim => claim.Mail.Id).Where(group => group.First().Mail.Gold > 0 &&
                group.Any(claim => claim.Attachment is null || claim.Attachment.Id == claim.Mail.Attachments[0].Id))
                .Select(group => group.First().Mail).ToArray();
            after = after with { Gold = checked(after.Gold + (uint)goldMails.Sum(mail => mail.Gold)) };
            var results = ImmutableArray.CreateBuilder<MailboxClaimChange>();
            foreach (var claim in claims)
            {
                if (claim.Attachment is { } attachment)
                    results.Add(new(claim.Mail.Id, attachment.ClaimId));
                else if (claim.Mail.Attachments.IsEmpty)
                    results.Add(new(claim.Mail.Id, claim.Mail.Id));
                else
                    foreach (var entry in claim.Mail.Attachments)
                        results.Add(new(claim.Mail.Id, entry.ClaimId));
            }
            return new(new(Receiver.Inventory, after, "mail_claim", new(Receiver)), claims, items,
                goldMails.Select(mail => mail.Id).ToImmutableArray(), Now, results.ToImmutable());
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
        public void Dispose() { OtherOwner.Dispose(); ReceiverOwner.Dispose(); SenderOwner.Dispose(); TestFiles.DeleteDirectory(root, true); }
    }
}
