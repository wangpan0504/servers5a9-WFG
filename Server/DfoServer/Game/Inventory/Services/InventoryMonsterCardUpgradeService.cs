using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryMonsterCardUpgradeService(ItemCatalog items, EnchantmentCatalog enchantments,
    MonsterCardBindCatalog cards, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, Func<int, int>? random = null)
{
    readonly Func<int, int> draw = random ?? Random.Shared.Next;

    public InventoryResult Apply(Character character, MonsterCardUpgradeRequest request,
        out MonsterCardUpgradeReceipt? receipt, out string? rejection, DateTimeOffset? utcNow = null)
    {
        receipt = null;
        rejection = null;
        if (request.MaterialSlots.IsDefaultOrEmpty || request.MaterialSlots.Length > 4 || request.TargetSlot is < 0 or > 351 ||
            request.MaterialSlots.Any(slot => slot is < 0 or > 351 || slot == request.TargetSlot) ||
            request.MaterialSlots.Distinct().Count() != request.MaterialSlots.Length)
            return Reject("invalid-slots", out rejection);
        var now = utcNow ?? DateTimeOffset.UtcNow;
        var before = character.Inventory;
        var target = before.Find(new(InventoryListType.Main, request.TargetSlot));
        if (!Valid(target, before, now, out var targetTemplate))
            return Reject("target-state", out rejection);
        int chance;
        try
        {
            if (target!.Core.EnchantUpgrade >= enchantments.CardMaximumLevel(target.Core.ItemId))
                return Reject("maximum-level", out rejection);
            chance = cards.CardUpgradeChance(targetTemplate!.Rarity, request.MaterialSlots.Length);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
        {
            return Reject("upgrade-resource", out rejection);
        }
        var materials = new List<InventoryItem>(request.MaterialSlots.Length);
        foreach (short slot in request.MaterialSlots)
        {
            var source = before.Find(new(InventoryListType.Main, slot));
            if (!Valid(source, before, now, out var template) || template!.Rarity != targetTemplate!.Rarity)
                return Reject("material-state", out rejection);
            materials.Add(source!);
        }

        var after = before;
        foreach (var source in materials)
        {
            var deleted = InventoryDeleteService.Plan(after, source.Position, source.Core.ItemId, 1);
            if (!deleted.Accepted)
                return Reject("material-changed", out rejection);
            after = deleted.Plan!.After;
        }
        // 先确认成功结果有位置，不能在无空间时抽取一次免费的升级结果。
        byte nextLevel = checked((byte)(target!.Core.EnchantUpgrade + 1));
        var changed = target with { Core = target.Core with { Value = 1, EnchantUpgrade = nextLevel } };
        var upgradedInventory = after;
        if (target.Core.Count == 1)
            upgradedInventory = after with { Items = after.Items.Replace(target, changed) };
        else
        {
            // 一次仅升级一张；其余堆叠仍保留原等级，完整实例属性和期限不重建。
            var deleted = InventoryDeleteService.Plan(after, target.Position, target.Core.ItemId, 1);
            if (!deleted.Accepted)
                return Reject("target-changed", out rejection);
            var granted = InventoryRewardGrantService.TryGrantItems(character with { Inventory = deleted.Plan!.After },
                [changed with { Uid = 0 }], items, goldLimits, pieces, now);
            if (!granted.Accepted)
                return Reject("card-capacity", out rejection);
            upgradedInventory = granted.Plan!.After;
        }
        int roll = draw(100);
        if (roll is < 0 or >= 100)
            return Reject("invalid-roll", out rejection);
        bool upgraded = roll < chance;
        byte level = upgraded ? nextLevel : target.Core.EnchantUpgrade;
        if (upgraded)
            after = upgradedInventory;
        long? deadline = null;
        foreach (var item in materials.Append(target))
            if (item.ExpireTime > 0 && (deadline is null || item.ExpireTime < deadline.Value))
                deadline = item.ExpireTime;
        receipt = new(upgraded, target.Core.ItemId, level);
        return new(true, 0, new(before, after, "monster_card_upgrade", new InventoryCharacterPrecondition(character, deadline)));
    }

    bool Valid(InventoryItem? item, InventorySnapshot inventory, DateTimeOffset now, out ItemTemplate? template)
    {
        template = null;
        if (item is null || item.Core.Count < 1 || !item.Core.IsStackable || item.Core.EquipmentLockId != 0 ||
            item.IsExpired(now.ToUnixTimeSeconds()))
            return false;
        try { template = items.Get(item.Core.ItemId); }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException) { return false; }
        return template.Id == item.Core.ItemId && template.Kind == item.Core.Kind && template.IsMonsterCard &&
            template.MonsterCardBindType is null && ItemSlotBoundService.Allows(inventory, item.Position, template.Kind);
    }

    static InventoryResult Reject(string reason, out string? rejection)
    {
        rejection = reason;
        return InventoryResult.Reject(22);
    }
}
