using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

// 设计图升级：消耗设计图本身与使用配方材料（不扣 NPC 售价 [need material]），把选中的装备换成同部位产物。
// 产物沿用原实例的 UID、槽位和全部核心字段，只替换模板 id，所以强化、增幅、附魔、徽章、
// 随机属性、符文与期限都随实例保留（设计图说明要求的正是这几项）。
internal sealed class InventoryUpgradeChronicleService(UpgradeChronicleCatalog recipes, ItemCatalog items,
    EpicPieceCatalog pieces, TimeProvider? clock = null)
{
    // 失败码沿用既有设计图路径。
    internal const int Rejection = 4;
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public InventoryResult Upgrade(Character character, InventorySlot blueprintPosition, int blueprintItemId,
        InventorySlot targetPosition, int targetItemId, DateTimeOffset? utcNow = null)
    {
        var before = character.Inventory;
        var now = utcNow ?? Clock.GetUtcNow();
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
        if (recipes.Get(blueprintItemId) is not { } definition || recipes.Find(definition, targetItemId) is not { } recipe ||
            recipe.OutputCount != 1 || recipe.OutputItemId <= 0 || recipe.OutputItemId == targetItemId)
            return InventoryResult.Reject(Rejection);
        var sourceTemplate = items.Get(blueprintItemId);
        if (sourceTemplate.Id != blueprintItemId || !sourceTemplate.AllowsUse(character.Level, character.Job) ||
            recipe.Materials.Any(material => material.ItemId == targetItemId))
            return InventoryResult.Reject(Rejection);
        var output = items.Get(recipe.OutputItemId);
        if (output.Id != recipe.OutputItemId || output.Kind != ItemKind.Equipment)
            return InventoryResult.Reject(Rejection);
        // 先扣设计图与材料再换产物：数组会缩短，但槽号不变；目标按真实 UID 重定位。
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
        // 新模板耐久上限可能低于原耐久（英雄之证明一类部位）；原样保留会得到40/18这种超上限实例，
        // 客户端据此把装备判为未修理并拒绝再次进化，所以按新上限钳制，不主动补满未损坏的装备。
        var upgraded = instance with
        {
            Core = instance.Core with
            {
                ItemId = recipe.OutputItemId,
                Durability = Math.Min(instance.Core.Durability, output.Durability)
            }
        };
        if (output.Echon is { Rules.IsModern: true })
        {
            // 转换保留打造，能力改用目标模板的固定初值，清除旧套装关联。
            var initial = InventoryRewardGrantService.Create(output, utcNow: now);
            upgraded = upgraded with { Core = upgraded.Core with { Extension114 = initial.Extension114, EchonSet = null } };
        }
        after = after with { Items = after.Items.SetItem(index, upgraded) };
        // 提交时复验角色/位置与本次实际用到的最早期限；不把未消耗材料纳入资格。
        var remaining = after.Items.ToDictionary(item => item.Uid);
        long? deadline = before.Items.Where(item => item.Uid == target.Uid ||
                !remaining.TryGetValue(item.Uid, out var current) || current.Core.Count != item.Core.Count)
            .Where(item => item.ExpireTime > 0).Select(item => (long?)item.ExpireTime).Min();
        return new(true, 0, new(before, after, "upgrade_chronicle",
            new(character, deadline, Clock, requirePosition: true)));
    }
}
