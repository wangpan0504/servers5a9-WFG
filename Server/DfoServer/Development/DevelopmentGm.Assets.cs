using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Items;
using System.Text.Json;

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
        var prototype = InventoryRewardGrantService.Create(template, utcNow: now, upgrade: (byte)request.Upgrade);
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
            "开发GM发放：道具 " + request.Primary + "，数量 " + request.Secondary + (request.Upgrade == 0 ? "" : "，强化 +" + request.Upgrade) + "。", 0, attachments, created, expires));
        if (!result.Accepted)
            throw new InvalidDataException("该道具无法通过实体邮件发放：" + result.Error);
        return "OK " + recipient.Name + " id=" + recipient.Id + " item=" + request.Primary + " count=" + request.Secondary + (request.Upgrade == 0 ? "" : " upgrade=" + request.Upgrade) +
            " mail=" + result.MailId + " 已发送到该角色邮箱，重新选择角色后领取。";
    }

    static Character LoadByName(ICharacterRepository repository, AccountOwners.Lease owner, string name)
    {
        var slot = repository.ListRoster(owner).SingleOrDefault(row => string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase))?.Slot
            ?? throw new InvalidDataException("角色不存在或不属于该账号");
        return repository.LoadCharacter(owner, slot);
    }

    static ItemTemplate? TryTemplate(ItemCatalog? items, int id)
    {
        if (items is null)
            return null;
        try { return items.Get(id); }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or KeyNotFoundException or FileNotFoundException)
        {
            return null;
        }
    }

    static string DescribeItem(int id, ItemCatalog items)
    {
        var template = TryTemplate(items, id) ?? throw new InvalidDataException("道具不存在或资源无法解析：" + id);
        return "OK " + JsonSerializer.Serialize(new
        {
            id = template.Id, name = template.Name, kind = template.Kind.ToString(), minLevel = template.MinimumLevel,
            rarity = template.Rarity, stackLimit = template.StackLimit,
            stackable = InventoryRewardGrantService.Create(template).IsStackable,
            upgradable = template.Kind == ItemKind.Equipment,
            refinable = template.Kind == ItemKind.Equipment && template.EquipmentSlot == 12
        });
    }

    // 只读背包视图：完整容器与位置，名称按需取自已加载模板，解析失败只留空名称，不影响其他行。
    static string DescribeInventory(Character character, ItemCatalog? items)
    {
        var inventory = character.Inventory;
        return "OK " + JsonSerializer.Serialize(new
        {
            name = character.Name, id = character.Id, rev = character.Revision,
            gold = inventory.Gold, reviveCoins = inventory.ReviveCoins, winPoints = inventory.WinPoints,
            expansion = inventory.Expansion, cargoCapacity = inventory.CargoCapacity,
            items = inventory.Items.OrderBy(item => item.Position.List).ThenBy(item => item.Position.Index).Select(item => new
            {
                list = (int)item.Position.List, listName = item.Position.List.ToString(), slot = item.Position.Index,
                uid = item.Uid, id = item.Core.ItemId, name = TryTemplate(items, item.Core.ItemId)?.Name,
                kind = item.Core.Kind.ToString(), count = item.Core.IsStackable ? item.Core.Value : 1,
                upgrade = item.Core.Kind == ItemKind.Equipment ? item.Core.Attr & 31 : 0, refine = item.Core.Kind == ItemKind.Equipment ? item.Core.GenuineUpgrade : 0,
                amplifyType = item.Core.AmplifyType, amplifyValue = item.Core.AmplifyValue, ability = item.Core.Kind == ItemKind.Avatar ? item.Core.Durability : 0,
                isSealed = item.Core.SealFlag != 0, expire = item.ExpireTime
            })
        });
    }
}
