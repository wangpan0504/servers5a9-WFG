using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Items;

namespace DfoServer.Development;

internal static partial class DevelopmentGm
{
    static Character SetGold(ICharacterRepository repository, AccountOwners.Lease owner, DevelopmentRequest request)
    {
        var slot = repository.ListRoster(owner).SingleOrDefault(row => string.Equals(row.Name, request.Name, StringComparison.OrdinalIgnoreCase))?.Slot
            ?? throw new InvalidDataException("角色不存在或不属于该账号");
        var character = repository.LoadCharacter(owner, slot);
        return InventoryService.Commit(repository, owner, character, CurrencyService.SetGold(character, (uint)request.Primary));
    }

    static string SendItem(DevelopmentRequest request, MailboxService mailbox, ItemCatalog items)
    {
        var recipient = mailbox.FindRecipient(request.Name);
        if (recipient is null || recipient.Account != request.Account)
            throw new InvalidDataException("角色不存在或不属于该账号");
        var template = items.Get(request.Primary);
        var now = DateTimeOffset.UtcNow;
        var prototype = InventoryRewardGrantService.Create(template, utcNow: now);
        bool stackable = prototype.IsStackable;
        if (stackable && request.Secondary > template.StackLimit)
            throw new InvalidDataException("数量超过该道具堆叠上限 " + template.StackLimit);
        if (!stackable && request.Secondary > 10)
            throw new InvalidDataException("非堆叠道具每封邮件最多发放10件");
        if (stackable)
            prototype = prototype with { Value = request.Secondary };
        var slot = new InventorySlot(ItemSlotBoundService.List(template.Kind), ItemSlotBoundService.Range(template.Kind, 0).Start);
        var attachments = Enumerable.Range(0, stackable ? 1 : request.Secondary)
            .Select(_ => InventoryRewardGrantService.CreateItem(template, slot, prototype)).ToImmutableArray();
        long created = now.ToUnixTimeSeconds();
        long expires = Math.Min(created + 15 * 86400, attachments.Where(item => item.ExpireTime > 0)
            .Select(item => (long)item.ExpireTime).DefaultIfEmpty(long.MaxValue).Min());
        var result = mailbox.Deliver(new("gm:" + Guid.NewGuid().ToString("N"), recipient, "开发GM",
            "开发GM发放：道具 " + request.Primary + "，数量 " + request.Secondary + "。", 0, attachments, created, expires));
        if (!result.Accepted)
            throw new InvalidDataException("该道具无法通过实体邮件发放：" + result.Error);
        return "OK " + recipient.Name + " id=" + recipient.Id + " item=" + request.Primary + " count=" + request.Secondary +
            " mail=" + result.MailId + " 已发送到该角色邮箱，重新选择角色后领取。";
    }
}
