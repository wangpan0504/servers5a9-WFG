using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

// CMD970 称号变更（绣线/华丽的绣线等）：按 etc/aradtitlechange_main.etc 与 etc/aradtitlechange_sub.etc
// 校验变更道具、封装状态、毕业排除、金币费用与目标称号，消耗 1 个变更道具及对应金币并在原槽位替换称号模板。
internal sealed class InventoryTitleChangeService(ItemCatalog items, TitleChangeCatalog titles,
    TimeProvider? time = null, Func<int, int>? nextRandom = null)
{
    public TimeProvider Clock { get; } = time ?? TimeProvider.System;
    readonly Func<int, int> random = nextRandom ?? Random.Shared.Next;

    public InventoryResult Apply(Character character, TitleChangeRequest request, out TitleChangeOutcome outcome,
        DateTimeOffset? utcNow = null)
    {
        outcome = default;
        var now = utcNow ?? Clock.GetUtcNow();
        long nowSeconds = now.ToUnixTimeSeconds();
        var before = character.Inventory;
        var sourceSlot = new InventorySlot(InventoryListType.Main, request.ThreadSlot);
        var targetSlot = new InventorySlot(InventoryListType.Main, request.TitleSlot);
        if (request.ThreadSlot < 0 || request.TitleSlot < 0 || sourceSlot == targetSlot)
            return InventoryResult.Reject(19);

        var source = before.Find(sourceSlot);
        if (source is not { Uid: > 0, Core.Kind: ItemKind.Consumable or ItemKind.Material } || source.IsExpired(nowSeconds) ||
            source.Core.EquipmentLockId != 0 || !source.Core.IsStackable ||
            !ItemSlotBoundService.Allows(before, sourceSlot, source.Core.Kind))
            return InventoryResult.Reject(17);

        var target = before.Find(targetSlot);
        if (target is not { Uid: > 0, Core.Kind: ItemKind.Equipment, Core.Count: 1, Avatar: null, Creature: null } ||
            target.Core.IsRental || target.IsExpired(nowSeconds) ||
            !ItemSlotBoundService.Allows(before, targetSlot, target.Core.Kind))
            return InventoryResult.Reject(19);
        if (target.Core.EquipmentLockId != 0)
            return InventoryResult.Reject(0xD5);

        ItemTemplate threadTemplate, titleTemplate;
        try
        {
            threadTemplate = items.Get(source.Core.ItemId);
            titleTemplate = items.Get(target.Core.ItemId);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException)
        {
            return InventoryResult.Reject(19);
        }

        if (!titles.TryGetMain(source.Core.ItemId, out var mainEntry) ||
            !threadTemplate.AllowsActivation(character.Level, character.Job) ||
            !InventoryMoveService.RequiredSkillSatisfied(character, threadTemplate))
            return InventoryResult.Reject(17);

        if (titleTemplate.Kind != ItemKind.Equipment || titleTemplate.EquipmentSlot != 13 ||
            !mainEntry.AllowsSealState(target.Core.SealFlag) ||
            mainEntry.FinalTitleItemIds.Contains(target.Core.ItemId) ||
            !mainEntry.ResourceGoldCosts.TryGetValue(target.Core.ItemId, out int goldCost) ||
            !titles.TryGetSub(target.Core.ItemId, out var subEntry))
            return InventoryResult.Reject(19);

        if (CurrencyWallet.Get(before, CurrencyKind.Gold) < goldCost)
            return InventoryResult.Reject(4);

        bool isSuccess = mainEntry.SuccessRate >= 10_000 ||
            (mainEntry.SuccessRate > 0 && random(10_000) < mainEntry.SuccessRate);
        int replacementId = subEntry.Select(isSuccess, random);
        ItemTemplate replacement;
        try
        {
            replacement = items.Get(replacementId);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException)
        {
            return InventoryResult.Reject(19);
        }
        if (replacement.Kind != ItemKind.Equipment || replacement.EquipmentSlot != 13)
            return InventoryResult.Reject(19);

        var paid = InventoryDeleteService.Plan(before, sourceSlot, source.Core.ItemId, 1);
        if (!paid.Accepted)
            return InventoryResult.Reject(17);
        var after = paid.Plan!.After;
        if (goldCost > 0)
            after = CurrencyWallet.Set(after, CurrencyKind.Gold, CurrencyWallet.Get(after, CurrencyKind.Gold) - goldCost);
        after = after with
        {
            Items = after.Items.Replace(target, target with
            {
                Core = target.Core with
                {
                    ItemId = replacementId,
                    Durability = Math.Min(target.Core.Durability, replacement.Durability)
                }
            })
        };

        long? deadline = new[] { source, target }.Where(item => item.ExpireTime > 0)
            .Select(item => (long?)item.ExpireTime).Min();
        outcome = new(isSuccess, replacementId, source.Core.ItemId);
        return new(true, 0, new InventoryPlan(before, after, "title_change",
            new(character, deadline, Clock, requirePosition: true))
        {
            UsedItems = [new(source.Core.ItemId, 1)]
        });
    }
}
