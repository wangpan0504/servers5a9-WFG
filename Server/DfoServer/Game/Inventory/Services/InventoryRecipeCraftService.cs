using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

// [recipe] 设计图制造：消耗设计图与配方材料，把配方指定的装备换成产物。
// 产物沿用原实例的 UID、槽位和全部核心字段，只替换模板 id，
// 因此 [maintain ability] / [maintain socket stat] 声明的强化、增幅、附魔、徽章与孔位都随之保留。
internal sealed class InventoryRecipeCraftService(ItemRecipeCatalog recipes, ItemCatalog items,
    EpicPieceCatalog pieces, TimeProvider? clock = null)
{
    // 客户端 25 的失败码尚未实测，先沿用同类制作业务的拒绝码 4。
    internal const int Rejection = 4;
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public InventoryResult Craft(Character character, InventorySlot blueprintPosition, out RecipeCraftReceipt? receipt,
        DateTimeOffset? utcNow = null)
    {
        receipt = null;
        var before = character.Inventory;
        var now = utcNow ?? (clock ?? TimeProvider.System).GetUtcNow();
        long seconds = now.ToUnixTimeSeconds();
        var blueprint = before.Find(blueprintPosition);
        if (blueprintPosition.List != InventoryListType.Main || blueprint is null || blueprint.Uid <= 0 ||
            blueprint.Core.Kind != ItemKind.Consumable || blueprint.Core.Count < 1 ||
            blueprint.Core.EquipmentLockId != 0 || blueprint.IsExpired(seconds))
            return InventoryResult.Reject(Rejection);
        if (recipes.Get(blueprint.Core.ItemId) is not { } definition || definition.OutputItemId <= 0 ||
            definition.OutputCount != 1 || definition.OutputItemId == definition.TargetItemId)
            return InventoryResult.Reject(Rejection);
        var output = items.Get(definition.OutputItemId);
        if (output.Id != definition.OutputItemId || output.Kind != ItemKind.Equipment)
            return InventoryResult.Reject(Rejection);
        // 被改造的装备按编号在 Main 里定位；锁定、过期和 detail 实例不参与制造。
        var target = before.Items
            .Where(item => item.Position.List == InventoryListType.Main && item.Core.ItemId == definition.TargetItemId &&
                item.Core.Count == 1 && item.Core.EquipmentLockId == 0 && !item.IsExpired(seconds) &&
                item.Avatar is null && item.Creature is null && item.Uid > 0)
            .OrderBy(item => item.Position.Index)
            .FirstOrDefault();
        if (target is null)
            return InventoryResult.Reject(Rejection);
        // 先扣设计图与材料再换产物：扣除会压缩容器，目标装备按 UID 重新定位，不依赖原槽位。
        var spentBlueprint = InventoryDeleteService.Plan(before, blueprintPosition, blueprint.Core.ItemId, 1);
        if (!spentBlueprint.Accepted || spentBlueprint.Plan is null)
            return InventoryResult.Reject(Rejection);
        var spent = InventoryMaterialConsumptionService.Plan(spentBlueprint.Plan.After, definition.Materials, pieces, now);
        if (!spent.Accepted || spent.Plan is null)
            return InventoryResult.Reject(Rejection);
        var after = spent.Plan.After;
        int index = Array.FindIndex(after.Items.AsSpan().ToArray(), item => item.Uid == target.Uid);
        if (index < 0)
            return InventoryResult.Reject(Rejection);
        var instance = after.Items[index];
        var crafted = instance with { Core = instance.Core with { ItemId = definition.OutputItemId } };
        after = after with { Items = after.Items.SetItem(index, crafted) };
        receipt = new(definition.OutputItemId, definition.OutputCount);
        return new(true, 0, new(before, after, "recipe_craft"));
    }
}

// ACK25 的成功行：产物编号、本次数量与提交后的 Main 槽位。
internal sealed record RecipeCraftReceipt(int ItemId, int Count);
