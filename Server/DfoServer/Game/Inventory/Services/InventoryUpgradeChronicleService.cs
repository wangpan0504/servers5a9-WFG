using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

// 设计图升级：消耗设计图本身、[need material] 与配方材料，把选中的装备换成同部位产物。
// 产物沿用原实例的 UID、槽位和全部核心字段，只替换模板 id，所以强化、增幅、附魔、徽章、
// 随机属性、符文与期限都随实例保留（设计图说明要求的正是这几项）。
internal sealed class InventoryUpgradeChronicleService(UpgradeChronicleCatalog recipes, ItemCatalog items,
    EpicPieceCatalog pieces, TimeProvider? clock = null)
{
    // 客户端 271 的失败正文只观察到成功形态（单个 0x01），失败码尚未实测，先沿用同类制作的 4。
    internal const int Rejection = 4;
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public InventoryResult Upgrade(Character character, InventorySlot blueprintPosition, int blueprintItemId,
        InventorySlot targetPosition, int targetItemId, DateTimeOffset? utcNow = null)
    {
        var before = character.Inventory;
        var now = utcNow ?? DateTimeOffset.UtcNow;
        long seconds = now.ToUnixTimeSeconds();
        var blueprint = before.Find(blueprintPosition);
        if (blueprintPosition.List != InventoryListType.Main || blueprint is null || blueprint.Uid <= 0 ||
            blueprint.Core.ItemId != blueprintItemId || blueprint.Core.Kind != ItemKind.Consumable ||
            blueprint.Core.Count < 1 || blueprint.Core.EquipmentLockId != 0 || blueprint.IsExpired(seconds) ||
            blueprint.Core.ItemId == targetItemId)
            return InventoryResult.Reject(Rejection);
        var target = before.Find(targetPosition);
        if (targetPosition.List != InventoryListType.Main || target is null || target.Uid <= 0 ||
            target.Core.ItemId != targetItemId || target.Core.Kind != ItemKind.Equipment || target.Core.Count != 1 ||
            target.Core.EquipmentLockId != 0 || target.IsExpired(seconds) || target.Avatar is not null || target.Creature is not null)
            return InventoryResult.Reject(Rejection);
        if (recipes.Get(blueprintItemId) is not { } definition || definition.Find(targetItemId) is not { } recipe ||
            recipe.OutputCount != 1 || recipe.OutputItemId <= 0 || recipe.OutputItemId == targetItemId)
            return InventoryResult.Reject(Rejection);
        var output = items.Get(recipe.OutputItemId);
        if (output.Id != recipe.OutputItemId || output.Kind != ItemKind.Equipment)
            return InventoryResult.Reject(Rejection);
        // 先扣设计图与材料再换产物：扣除会压缩容器，目标装备按 UID 重新定位，不依赖请求里的槽位。
        var spentBlueprint = InventoryDeleteService.Plan(before, blueprintPosition, blueprintItemId, 1);
        if (!spentBlueprint.Accepted || spentBlueprint.Plan is null)
            return InventoryResult.Reject(Rejection);
        var spent = InventoryMaterialConsumptionService.Plan(spentBlueprint.Plan.After, recipe.Materials, pieces, now);
        if (!spent.Accepted || spent.Plan is null)
            return InventoryResult.Reject(Rejection);
        var after = spent.Plan.After;
        int index = Array.FindIndex(after.Items.AsSpan().ToArray(), item => item.Uid == target.Uid);
        if (index < 0)
            return InventoryResult.Reject(Rejection);
        var instance = after.Items[index];
        var upgraded = instance with { Core = instance.Core with { ItemId = recipe.OutputItemId } };
        after = after with { Items = after.Items.SetItem(index, upgraded) };
        return new(true, 0, new(before, after, "upgrade_chronicle"));
    }
}
