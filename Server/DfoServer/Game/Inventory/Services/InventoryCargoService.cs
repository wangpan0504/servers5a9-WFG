using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryCargoService(ItemCatalog catalog, CargoCatalog cargo, GoldLimitCatalog goldLimits, TimeProvider? clock = null)
{
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public static bool AllowsInsert(ItemCore core, InventoryListType list, ItemTemplate template)
    {
        string attach = template.AttachType.Replace("`", "").Trim().Trim('[', ']').Trim();
        return list switch
        {
            InventoryListType.PersonalCargo => !template.PersonalCargoForbidden && !attach.Equals("trade delete", StringComparison.OrdinalIgnoreCase),
            InventoryListType.AccountCargo => !template.PvpFreeEquipment && !template.AccountCargoForbidden && core.EquipmentLockId == 0 && core.TradeRestriction != 1 &&
                (attach.Equals("free", StringComparison.OrdinalIgnoreCase) || attach.Equals("account", StringComparison.OrdinalIgnoreCase) ||
                 attach.Equals("sealing", StringComparison.OrdinalIgnoreCase) ||
                 attach.Equals("trade limit", StringComparison.OrdinalIgnoreCase) && core.Attr >> 5 != 0 ||
                 attach.Length == 0 && template.AccountBoundAfterTrade),
            _ => true
        };
    }

    public InventoryResult AccountUpgrade(Character character, bool create)
    {
        var before = character.Inventory;
        if (create != (before.AccountCargo.Capacity == 0))
            return InventoryResult.Reject(create ? (ushort)20 : (ushort)21);
        if (create && character.Level < cargo.RequiredLevel)
            return InventoryResult.Reject(14);
        var tier = cargo.Next(before.AccountCargo.Capacity);
        if (tier is null)
            return InventoryResult.Reject(19);
        // 空306没有商品或付款凭据；付费档只接受当前下一档的历史预付工具。
        if (tier.ProductId > 0)
            return PaidUpgrade(character, catalog, tier, Clock.GetUtcNow(), true, out _, Clock);
        if (before.Gold < tier.GoldCost)
            return InventoryResult.Reject(20);
        var after = before with { Gold = checked(before.Gold - (uint)tier.GoldCost) };
        int remaining = tier.MaterialCount;
        long now = Clock.GetUtcNow().ToUnixTimeSeconds();
        long? deadline = null;
        foreach (var item in before.Items.Where(item => item.Position.List == InventoryListType.Main && item.Core.ItemId == tier.MaterialId &&
                     item.Core.IsStackable && item.Core.EquipmentLockId == 0 && !item.Core.IsExpired(now)).OrderBy(item => item.Position.Index))
        {
            int consumed = Math.Min(remaining, item.Core.Count);
            if (consumed == 0)
                break;
            var deletion = InventoryDeleteService.Plan(after, item.Position, item.Core.Value, consumed);
            if (!deletion.Accepted)
                return InventoryResult.Reject(20);
            after = deletion.Plan!.After;
            remaining -= consumed;
            if (item.ExpireTime > 0 && (deadline is null || item.ExpireTime < deadline)) deadline = item.ExpireTime;
        }
        if (remaining != 0)
            return InventoryResult.Reject(20);
        after = after with { AccountCargo = before.AccountCargo with { Capacity = tier.Capacity } };
        return new(true, 0, new(before, after, create ? "account_cargo_create" : "account_cargo_upgrade", new(character, deadline, Clock)));
    }

    // 只规划容量与可选预付凭据；新购买的点券由商城整单统一扣除。
    internal static InventoryResult PaidUpgrade(Character character, ItemCatalog catalog, AccountCargoTier tier,
        DateTimeOffset now, bool requireTool, out InventoryItem? tool, TimeProvider? clock = null)
    {
        tool = null;
        var before = character.Inventory;
        if (tier.ProductId <= 0 || tier.ToolItemId <= 0 || tier.CeraCost <= 0 || tier.GoldCost != 0 || tier.MaterialCount != 0 ||
            tier.Capacity <= before.AccountCargo.Capacity)
            return InventoryResult.Reject(17);
        var template = catalog.Get(tier.ToolItemId);
        if (!template.AllowsActivation(character.Level, character.Job) || !InventoryMoveService.RequiredSkillSatisfied(character, template))
            return InventoryResult.Reject(17);
        tool = before.Items.Where(item => item.Position.List == InventoryListType.Main && item.Core.ItemId == tier.ToolItemId &&
            item.Core.Kind == template.Kind && item.Core.IsStackable && item.Core.EquipmentLockId == 0 && !item.IsExpired(now.ToUnixTimeSeconds()))
            .OrderBy(item => item.Position.Index).FirstOrDefault();
        if (requireTool && tool is null)
            return InventoryResult.Reject(20);
        var after = before;
        if (tool is not null)
        {
            var consumed = InventoryDeleteService.Plan(before, tool.Position, tool.Core.Value, 1);
            if (!consumed.Accepted) return InventoryResult.Reject(17);
            after = consumed.Plan!.After;
        }
        after = after with { AccountCargo = before.AccountCargo with { Capacity = tier.Capacity } };
        return new(true, 0, new(before, after, "account_cargo_upgrade", new(character, tool?.ExpireTime > 0 ? tool.ExpireTime : null, clock)));
    }

    public InventoryResult Gold(Character character, int amount, bool deposit)
    {
        var before = character.Inventory;
        if (amount <= 0 || before.AccountCargo.Capacity == 0)
            return InventoryResult.Reject(10);
        long characterGold = (long)before.Gold + (deposit ? -amount : amount);
        long cargoGold = (long)before.AccountCargo.Gold + (deposit ? amount : -amount);
        int carryLimit = new CurrencyService(goldLimits).CarryLimit(character);
        if (characterGold < 0 || characterGold > uint.MaxValue || cargoGold < 0 || cargoGold > int.MaxValue ||
            deposit && cargoGold > cargo.GoldLimit(before.AccountCargo.Capacity) || !deposit && characterGold > carryLimit)
            return InventoryResult.Reject(10);
        var after = before with { Gold = checked((uint)characterGold), AccountCargo = before.AccountCargo with { Gold = (int)cargoGold } };
        return new(true, 0, new(before, after, deposit ? "account_cargo_deposit_gold" : "account_cargo_withdraw_gold"));
    }

    public InventoryResult PersonalUpgrade(Character character, short slot)
    {
        var before = character.Inventory;
        var item = before.Find(new(InventoryListType.Main, slot));
        if (item is null || !item.Core.IsStackable || item.Core.EquipmentLockId != 0 || item.Core.IsExpired(Clock.GetUtcNow().ToUnixTimeSeconds()))
            return InventoryResult.Reject(17);
        var template = catalog.Get(item.Core.ItemId);
        if (!template.AllowsActivation(character.Level, character.Job) || !InventoryMoveService.RequiredSkillSatisfied(character, template))
            return InventoryResult.Reject(17);
        ushort target = template.PersonalCargoUpgradeTarget;
        if (template.AdvancesPersonalCargo)
        {
            // 无参数的逐档券只沿本版17档容量表提升一档，不能替表外旧容量进位。
            if (!CargoCatalog.SupportsPersonalCapacity(before.CargoCapacity) || before.CargoCapacity >= 264)
                return InventoryResult.Reject(19);
            target = checked((ushort)(before.CargoCapacity + 16));
        }
        if (target <= before.CargoCapacity || !CargoCatalog.SupportsPersonalCapacity(target))
            return InventoryResult.Reject(19);
        var consumed = InventoryDeleteService.Plan(before, item.Position, item.Core.Value, 1);
        if (!consumed.Accepted)
            return InventoryResult.Reject(17);
        var after = consumed.Plan!.After with { CargoCapacity = target };
        return new(true, 0, new(before, after, "personal_cargo_upgrade",
            new(character, item.ExpireTime > 0 ? item.ExpireTime : null, Clock)));
    }

    public InventoryResult MoveToAccountCargo(Character character, InventoryListType sourceList, short sourceSlot, int count = 0)
    {
        var before = character.Inventory;
        if (before.AccountCargo.Capacity == 0 || sourceList == InventoryListType.AccountCargo)
            return InventoryResult.Reject(23);
        var source = before.Find(new(sourceList, sourceSlot));
        if (source is null)
            return InventoryResult.Reject(23);
        int moveCount = count <= 0 ? source.Core.Count : count;
        var request = new InventoryMoveRequest(
            source.Position, source.Core.ItemId, moveCount,
            new(InventoryListType.AccountCargo, 0), 0, 0, 0, 0, 0);
        return InventoryMoveService.Plan(character, request, catalog, Clock.GetUtcNow().ToUnixTimeSeconds());
    }
}
