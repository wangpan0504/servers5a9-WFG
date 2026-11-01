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
    public static CurrencyKind? MaterialCurrency(int itemId) => CurrencyWallet.RewardKind(itemId) is { } kind &&
        kind is >= CurrencyKind.BlackCube and <= CurrencyKind.GoldCube ? kind : null;
    public static CurrencyKind? AttachmentCurrency(int itemId) => MaterialCurrency(itemId) ?? CurrencyWallet.TokenKind(itemId);
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
            request.Special != 0 || request.RecipientServerIndex != 0 || request.Text is null || request.Text.Contains('\0') ||
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
            // 原生02DFF2C0把来源容器号直接保存到附件Type，02DFFC20原样发送。
            InventoryListType? list = input.Type switch
            {
                0 or 2 => InventoryListType.Main, 1 => InventoryListType.Avatar,
                3 or 7 => InventoryListType.Pet, 46 => InventoryListType.Talisman, _ => null
            };
            if (list == InventoryListType.Talisman && input.Slot is < 0 or >= 210)
                return MailboxError.InvalidAttachment;
            if (list is null || input.Count <= 0)
                return MailboxError.InvalidAttachment;
            var slot = new InventorySlot(list.Value, input.Slot);
            if (!seen.Add(slot))
                return MailboxError.InvalidAttachment;
            var currency = MaterialCurrency(input.ItemId);
            ItemTemplate template;
            InventoryItem? item;
            if (currency is { } kind)
            {
                template = items.Get(input.ItemId);
                if (slot.List != InventoryListType.Main || ItemSlotBoundService.CurrencySlot(kind) != slot.Index ||
                    template.Kind != ItemKind.Material || CurrencyWallet.Get(after, kind) < input.Count)
                    return MailboxError.InvalidAttachment;
                // 此对象仅描述待托管的邮件数量，不加入角色物品栏，也不借用一个假库存UID。
                item = new(0, slot, InventoryRewardGrantService.Create(template, utcNow: now) with { Value = input.Count });
            }
            else
            {
                item = after.Find(slot);
                if (item is null || item.Core.ItemId != input.ItemId || input.Count > item.Core.Count ||
                    !item.Core.IsStackable && input.Count != 1 || CurrencyWallet.RewardKind(input.ItemId) is not null || pieces.ByPiece.ContainsKey(input.ItemId))
                    return MailboxError.InvalidAttachment;
                template = items.Get(item.Core.ItemId);
            }
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
            InventoryItem? remaining = null;
            if (currency is { } walletKind)
                after = CurrencyService.Spend(after, walletKind, input.Count).Plan!.After;
            else
            {
                if (!InventoryDeleteService.TryDelete(item, item.Core.Value, input.Count, out remaining))
                    return MailboxError.InvalidAttachment;
                after = after with { Items = remaining is null ? after.Items.Remove(item) : after.Items.Replace(item, remaining) };
            }
            var core = item.Core.IsStackable ? item.Core with { Value = input.Count } : item.Core;
            if (Normalize(template.AttachType) == "trade limit")
                core = core with { Attr = (byte)((core.Attr & 31) | (((core.Attr >> 5) - 1) << 5)) };
            core = StackableTradePolicy.AfterTransfer(core, template);
            attachments.Add(item with { Uid = remaining is null ? item.Uid : 0, Core = core });
        }
        after = after with { Gold = checked((uint)(character.Inventory.Gold - cost)) };
        plan = new(recipient, request, new(character.Inventory, after, "mail_send", new(character)), attachments.ToImmutable(),
            checked((uint)fee), Day(now), now.ToUnixTimeSeconds());
        return MailboxError.None;
    }

    public static MailboxError TransferError(InventoryItem item, ItemTemplate template, string senderAccount, string recipientAccount, long now)
        => TransferError(item, template, senderAccount == recipientAccount, now);

    public static MailboxError TransferError(InventoryItem item, ItemTemplate template, bool sameAccount, long now)
    {
        if (item.Core.SortLockFlag != 0 || item.Core.EquipmentLockId != 0)
            return MailboxError.LockedItem;
        if (item.IsExpired(now))
            return MailboxError.ExpiredItem;
        if (item.Core.IsRental || item.ExpireTime > 0)
            return MailboxError.LimitedPeriod;
        if (template.Id != item.Core.ItemId || template.Kind != item.Core.Kind)
            return MailboxError.InvalidAttachment;
        if (template.PvpFreeEquipment || item.Core.TradeRestriction != 0)
            return MailboxError.NotTradable;
        if (StackableTradePolicy.Applies(item.Core, template))
            return StackableTradePolicy.CanTransfer(item.Core, template, sameAccount)
                ? MailboxError.None : MailboxError.AccountBound;
        string attach = Normalize(template.AttachType);
        return attach switch
        {
            "free" => MailboxError.None,
            "trade limit" => item.Core.Attr >> 5 != 0 ? MailboxError.None : MailboxError.NotTradable,
            "sealing" or "seal" => item.Core.SealFlag != 0 ? MailboxError.None : MailboxError.NotTradable,
            _ when attach.Contains("account", StringComparison.Ordinal) => sameAccount ? MailboxError.None : MailboxError.AccountBound,
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
        var targets = ImmutableArray.CreateBuilder<MailboxClaim>();
        var seen = new HashSet<long>();
        foreach (var claim in claims)
        {
            var mail = claim.Mail;
            var attachments = claim.Attachment is null ? mail.Attachments : [claim.Attachment];
            if (attachments.IsEmpty)
            {
                if (mail.Gold == 0)
                    return MailboxError.MailMissing;
                targets.Add(new(mail.Id, mail, null));
            }
            foreach (var attachment in attachments)
            {
                if (!seen.Add(attachment.Item.Uid))
                    return MailboxError.InvalidRequest;
                if (attachment.Item.IsExpired(now.ToUnixTimeSeconds()))
                    return MailboxError.ExpiredItem;
                targets.Add(new(attachment.ClaimId, mail, attachment));
            }
        }
        var ordered = targets.ToImmutable();
        var error = PlanClaimPrefix(character, claims, ordered, ordered.Length, now, out plan);
        if (error == MailboxError.None)
            return error;

        // 发放不腾空既有槽位、也不扣金币，可领前缀单调；正常整批只规划一次，失败再二分，避免逐件重扫背包。
        int low = 0, high = ordered.Length;
        MailboxClaimPlan? accepted = null;
        while (high - low > 1)
        {
            int count = low + (high - low) / 2;
            var candidateError = PlanClaimPrefix(character, claims, ordered, count, now, out var candidate);
            if (candidateError == MailboxError.None)
            {
                low = count;
                accepted = candidate;
            }
            else
            {
                high = count;
                error = candidateError;
            }
        }
        if (accepted is null)
            return error;
        var failed = ordered[low];
        plan = accepted with { Results = accepted.Results.Add(new(failed.Mail.Id, failed.ClaimId, error)) };
        return MailboxError.None;
    }

    MailboxError PlanClaimPrefix(Character character, ImmutableArray<MailboxClaim> claims, ImmutableArray<MailboxClaim> targets,
        int count, DateTimeOffset now, out MailboxClaimPlan? plan)
    {
        plan = null;
        var grants = ImmutableArray.CreateBuilder<InventoryItem>();
        var goldMails = ImmutableArray.CreateBuilder<uint>();
        var results = ImmutableArray.CreateBuilder<MailboxClaimChange>();
        long gold = 0;
        for (int index = 0; index < count; index++)
        {
            var target = targets[index];
            var mail = target.Mail;
            if (mail.Gold > 0 && (target.Attachment is null || target.Attachment.Id == mail.Attachments[0].Id))
            {
                gold += mail.Gold;
                goldMails.Add(mail.Id);
            }
            if (target.Attachment is { } attachment)
                grants.Add(attachment.Item);
            results.Add(new(mail.Id, target.ClaimId));
        }
        if (gold != 0 && gold > new CurrencyService(goldLimits).CarryLimit(character) - (long)character.Inventory.Gold)
            return MailboxError.GoldCarryLimit;
        var received = grants.ToImmutable();
        foreach (var group in received.Where(item => AttachmentCurrency(item.Core.ItemId).HasValue)
            .GroupBy(item => AttachmentCurrency(item.Core.ItemId)!.Value))
            if (CurrencyWallet.Get(character.Inventory, group.Key) + group.Sum(item => (long)item.Core.Count) > int.MaxValue)
                return MailboxError.InventoryFull;
        var grant = InventoryRewardGrantService.TryGrantItems(character, received, items, goldLimits, pieces, now);
        if (!grant.Accepted)
            return MailboxError.InventoryFull;
        var after = grant.Plan!.After with { Gold = checked((uint)(grant.Plan.After.Gold + gold)) };
        plan = new(new(character.Inventory, after, "mail_claim", grant.Plan.CharacterPrecondition ?? new(character)), claims, received, goldMails.ToImmutable(),
            now.ToUnixTimeSeconds(), results.ToImmutable());
        return MailboxError.None;
    }
    public MailboxResult Claim(AccountOwners.Lease owner, Character character, MailboxClaimPlan plan) => store.ClaimMail(owner, character, plan);
    // 只读查看：不触发过期清理，供开发GM在原 owner 内读取。
    public MailboxPage Inspect(AccountOwners.Lease owner, ushort characterId, DateTimeOffset now) => store.LoadMailbox(owner, characterId, now.ToUnixTimeSeconds());
    public MailboxResult ChangeState(AccountOwners.Lease owner, ushort characterId, ImmutableArray<uint> ids, ushort state, DateTimeOffset now, bool force = false) =>
        store.ChangeMailState(owner, characterId, ids, state, now.ToUnixTimeSeconds(), force);
    public MailboxResult Deliver(MailboxDelivery delivery)
    {
        if (delivery.Items.IsDefault || delivery.Items.Length > 10)
            return MailboxResult.Reject(MailboxError.InvalidRequest);
        foreach (var item in delivery.Items)
        {
            if (item.IsExpired(delivery.Created))
                return MailboxResult.Reject(MailboxError.ExpiredItem);
            var walletMaterial = AttachmentCurrency(item.Core.ItemId).HasValue;
            // 晶块和两类代币券仅托管面额，领取回账号钱包；不放行其他货币或旧背包实物。
            if (CurrencyWallet.RewardKind(item.Core.ItemId) is not null && !walletMaterial || pieces.ByPiece.ContainsKey(item.Core.ItemId))
                return MailboxResult.Reject(MailboxError.InvalidAttachment);
            var template = items.Get(item.Core.ItemId);
            bool legacyRune = item.Core.Kind == ItemKind.Consumable && template.Kind == ItemKind.Rune;
            if (!InventoryKindCompatibility.AcceptsKind(item.Core.Kind, template) || !walletMaterial && !legacyRune && item.Core.Count > template.StackLimit ||
                walletMaterial && (template.Kind != ItemKind.Material || item.ExpireTime != 0))
                return MailboxResult.Reject(MailboxError.InvalidAttachment);
        }
        return store.DeliverMail(delivery);
    }
}
