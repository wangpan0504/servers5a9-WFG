using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Mailbox;

internal sealed class MailboxService(IMailboxStore store, ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces)
{
    public const int PageSize = 20;
    public const int StorageLimit = 10;
    public static long Fee(uint gold, int attachments, long attachmentValue = 0) =>
        checked(100L + attachments * 1000L + gold / 20L + attachmentValue / 20);
    public static long DailyGoldLimit(int level) => 10_000L * Math.Max(1, level) * Math.Max(1, level);
    // 与其他按 UTC+8 06:00 刷新的日业务使用相同边界，不依赖宿主时区。
    public static int Day(DateTimeOffset now) => int.Parse(now.UtcDateTime.AddHours(2).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));

    public MailboxRecipient? FindRecipient(string name) => store.FindMailRecipient(name);
    public MailboxPage Open(AccountOwners.Lease owner, ushort characterId, DateTimeOffset now)
    {
        store.ExpireMailbox(owner, characterId, now.ToUnixTimeSeconds());
        return store.LoadMailbox(owner, characterId, now.ToUnixTimeSeconds());
    }

    public MailboxError PlanSend(Character character, string account, MailboxSendRequest request, DateTimeOffset now, out MailboxSendPlan? plan)
    {
        plan = null;
        if (!CharacterName.IsValid(request.Recipient) || request.Items.IsDefault || request.Items.Length > 10 ||
            request.Special != 0 || request.Global != 0 || request.Text is null || request.Text.Contains('\0') ||
            CharacterName.Encoding.GetByteCount(request.Text) > 511)
            return MailboxError.InvalidRequest;
        var recipient = store.FindMailRecipient(request.Recipient);
        if (recipient is null)
            return MailboxError.RecipientMissing;
        if (recipient.Id == character.Id)
            return MailboxError.SelfSend;
        if (request.Gold == 0 && request.Items.Length == 0 && request.Text.Length == 0)
            return MailboxError.Empty;
        if (request.Gold > goldLimits.CarryLimit(recipient.Level))
            return MailboxError.RecipientGoldLimit;
        long fee = Fee(request.Gold, request.Items.Length);
        long cost = (long)request.Gold + fee;
        if (cost > character.Inventory.Gold)
            return MailboxError.InsufficientGold;

        var after = character.Inventory;
        long attachmentValue = 0;
        var attachments = ImmutableArray.CreateBuilder<InventoryItem>();
        var seen = new HashSet<InventorySlot>();
        foreach (var input in request.Items)
        {
            InventoryListType? list = input.Type switch { 0 or 2 => InventoryListType.Main, 1 => InventoryListType.Avatar, 3 or 7 => InventoryListType.Pet, _ => null };
            if (list is null || input.Count <= 0)
                return MailboxError.InvalidAttachment;
            var slot = new InventorySlot(list.Value, input.Slot);
            var item = after.Find(slot);
            if (!seen.Add(slot) || item is null || item.Core.ItemId != input.ItemId || input.Count > item.Core.Count ||
                !item.Core.IsStackable && input.Count != 1 || CurrencyWallet.RewardKind(input.ItemId) is not null || pieces.ByPiece.ContainsKey(input.ItemId))
                return MailboxError.InvalidAttachment;
            var template = items.Get(input.ItemId);
            var error = TransferError(item, template, account, recipient.Account, now.ToUnixTimeSeconds());
            if (error != MailboxError.None)
                return error;
            // 客户端以出售单价（装备含耐久折价）乘寄送数量，合计后另收5%。
            attachmentValue += (long)template.ShopPrice.GetSellGold(item.Core.Durability, template.Durability) * input.Count;
            fee = Fee(request.Gold, request.Items.Length, attachmentValue);
            cost = request.Gold + fee;
            // 每次累加即检查余额，避免恶意大数量继续累加溢出；失败没有持久化副作用。
            if (cost > character.Inventory.Gold)
                return MailboxError.InsufficientGold;
            if (!InventoryDeleteService.TryDelete(item, item.Core.Value, input.Count, out var remaining))
                return MailboxError.InvalidAttachment;
            after = after with { Items = remaining is null ? after.Items.Remove(item) : after.Items.Replace(item, remaining) };
            var core = item.Core.IsStackable ? item.Core with { Value = input.Count } : item.Core;
            if (Normalize(template.AttachType) == "trade limit")
                core = core with { Attr = (byte)((core.Attr & 31) | (((core.Attr >> 5) - 1) << 5)) };
            attachments.Add(item with { Uid = remaining is null ? item.Uid : 0, Core = core });
        }
        after = after with { Gold = checked((uint)(character.Inventory.Gold - cost)) };
        plan = new(recipient, request, new(character.Inventory, after, "mail_send", new(character)), attachments.ToImmutable(),
            checked((uint)fee), Day(now), now.ToUnixTimeSeconds());
        return MailboxError.None;
    }

    public static MailboxError TransferError(InventoryItem item, ItemTemplate template, string senderAccount, string recipientAccount, long now)
    {
        if (item.Core.SortLockFlag != 0 || item.Core.EquipmentLockId != 0)
            return MailboxError.LockedItem;
        if (item.IsExpired(now))
            return MailboxError.ExpiredItem;
        if (item.Core.IsRental || item.ExpireTime > 0)
            return MailboxError.LimitedPeriod;
        if (template.Id != item.Core.ItemId || template.Kind != item.Core.Kind)
            return MailboxError.InvalidAttachment;
        if (item.Core.TradeRestriction != 0)
            return MailboxError.NotTradable;
        string attach = Normalize(template.AttachType);
        return attach switch
        {
            "free" => MailboxError.None,
            "trade limit" => item.Core.Attr >> 5 != 0 ? MailboxError.None : MailboxError.NotTradable,
            "sealing" or "seal" => item.Core.SealFlag != 0 ? MailboxError.None : MailboxError.NotTradable,
            _ when attach.Contains("account", StringComparison.Ordinal) => senderAccount == recipientAccount ? MailboxError.None : MailboxError.AccountBound,
            _ => MailboxError.NotTradable
        };
    }
    static string Normalize(string value) => value.Replace("`", "").Trim().Trim('[', ']').Trim().ToLowerInvariant();

    public MailboxResult Send(AccountOwners.Lease owner, Character character, MailboxSendPlan plan) => store.SendMail(owner, character, plan);

    public MailboxError PlanClaim(AccountOwners.Lease owner, Character character, ImmutableArray<uint> ids, DateTimeOffset now, out MailboxClaimPlan? plan)
    {
        plan = null;
        var claims = store.LoadMailClaims(owner, character.Id, ids, now.ToUnixTimeSeconds());
        if (claims.Length != ids.Length)
            return MailboxError.MailMissing;
        var grants = ImmutableArray.CreateBuilder<InventoryItem>();
        var goldMails = ImmutableArray.CreateBuilder<uint>();
        var seen = new HashSet<long>();
        long gold = 0;
        foreach (var group in claims.GroupBy(claim => claim.Mail.Id))
        {
            var mail = group.First().Mail;
            if (mail.Gold > 0 && group.Any(claim => claim.Attachment is null || claim.Attachment.Id == mail.Attachments[0].Id))
            {
                gold += mail.Gold;
                goldMails.Add(mail.Id);
            }
            foreach (var claim in group)
            {
                var attachments = claim.Attachment is null ? mail.Attachments : [claim.Attachment];
                if (mail.Gold == 0 && attachments.Length == 0)
                    return MailboxError.MailMissing;
                foreach (var attachment in attachments)
                {
                    if (!seen.Add(attachment.Item.Uid))
                        return MailboxError.InvalidRequest;
                    if (attachment.Item.IsExpired(now.ToUnixTimeSeconds()))
                        return MailboxError.ExpiredItem;
                    grants.Add(attachment.Item);
                }
            }
        }
        if (gold != 0 && gold > new CurrencyService(goldLimits).CarryLimit(character) - (long)character.Inventory.Gold)
            return MailboxError.GoldCarryLimit;
        var grant = InventoryRewardGrantService.TryGrantItems(character, grants.ToImmutable(), items, goldLimits, pieces, now);
        if (!grant.Accepted)
            return MailboxError.InventoryFull;
        var after = grant.Plan!.After with { Gold = checked((uint)(grant.Plan.After.Gold + gold)) };
        plan = new(new(character.Inventory, after, "mail_claim", new(character)), claims, grants.ToImmutable(), goldMails.ToImmutable(), now.ToUnixTimeSeconds());
        return MailboxError.None;
    }
    public MailboxResult Claim(AccountOwners.Lease owner, Character character, MailboxClaimPlan plan) => store.ClaimMail(owner, character, plan);
    public MailboxResult ChangeState(AccountOwners.Lease owner, ushort characterId, ImmutableArray<uint> ids, ushort state, DateTimeOffset now) =>
        store.ChangeMailState(owner, characterId, ids, state, now.ToUnixTimeSeconds());
    public MailboxResult Deliver(MailboxDelivery delivery)
    {
        if (delivery.Items.IsDefault || delivery.Items.Length > 10)
            return MailboxResult.Reject(MailboxError.InvalidRequest);
        foreach (var item in delivery.Items)
        {
            if (item.IsExpired(delivery.Created))
                return MailboxResult.Reject(MailboxError.ExpiredItem);
            // 钱包/碎片按真实 owner 分流；当前邮箱只托管普通实体，不新建虚拟资产实例。
            if (CurrencyWallet.RewardKind(item.Core.ItemId) is not null || pieces.ByPiece.ContainsKey(item.Core.ItemId))
                return MailboxResult.Reject(MailboxError.InvalidAttachment);
            var template = items.Get(item.Core.ItemId);
            if (template.Kind != item.Core.Kind || item.Core.Count > template.StackLimit)
                return MailboxResult.Reject(MailboxError.InvalidAttachment);
        }
        return store.DeliverMail(delivery);
    }
}
