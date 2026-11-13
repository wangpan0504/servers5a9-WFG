using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

// 设计图升级：消耗设计图本身与使用配方材料（不扣 NPC 售价 [need material]），把选中的装备换成同部位产物。
// 产物沿用原实例身份，打造字段按卷轴族分别保留、封顶或重置。
internal sealed class InventoryUpgradeChronicleService(UpgradeChronicleCatalog recipes, ItemCatalog items,
    EpicPieceCatalog pieces, TimeProvider? clock = null)
{
    // 失败码沿用既有设计图路径。
    internal const int Rejection = 4;
    internal const string LevelReason = "upgrade_chronicle_level";
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public InventoryResult Upgrade(Character character, InventorySlot blueprintPosition, int blueprintItemId,
        InventorySlot targetPosition, int targetItemId, DateTimeOffset? utcNow = null,
        InventorySlot? materialPosition = null, int materialItemId = 0)
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
        if (recipes.Get(blueprintItemId) is not { } definition)
            return InventoryResult.Reject(Rejection);
        var recipe = recipes.Find(definition, targetItemId);
        int levelIncrease = 0;
        if (definition.LevelTicket is { } level)
        {
            var template = items.Get(targetItemId);
            int effective = template.MinimumLevel + unchecked((sbyte)target.Core.EmancipateLevel);
            if (template.Id != targetItemId || template.Rarity != level.Rarity || effective < level.Minimum || effective >= level.Maximum ||
                level.Excluded.Contains(targetItemId) || target.Core.IsRental ||
                recipes.LevelCost(template.Rarity, template.EquipmentSlot, effective,
                    target.Core.AmplifyType == 0 ? target.Core.Upgrade : 0,
                    target.Core.AmplifyType == 0 ? 0 : target.Core.Upgrade, target.Core.GenuineUpgrade & 0x7F) is not { Count: > 0 } cost)
                return InventoryResult.Reject(Rejection);
            levelIncrease = Math.Min(level.Increase, level.Maximum - effective);
            if (unchecked((sbyte)target.Core.EmancipateLevel) + levelIncrease > sbyte.MaxValue)
                return InventoryResult.Reject(Rejection);
            recipe = new(0, [targetItemId], [cost], targetItemId, 1);
        }
        if (recipe is null || recipe.OutputCount != 1 || recipe.OutputItemId <= 0 ||
            recipe.OutputItemId == targetItemId && levelIncrease == 0) return InventoryResult.Reject(Rejection);
        if (materialPosition is { } material)
        {
            // 客户端附带费用表首行的一个持有槽；这里只交叉核验，不按提示再扣第二次。
            var referenced = before.Find(material);
            if (definition.LevelTicket is null || recipe.Materials.IsEmpty || recipe.Materials[0].ItemId != materialItemId ||
                material.List != InventoryListType.Main || referenced is not { Uid: > 0 } || referenced.Core.ItemId != materialItemId ||
                referenced.Core.EquipmentLockId != 0 || referenced.IsExpired(seconds)) return InventoryResult.Reject(Rejection);
        }
        else if (materialItemId != 0) return InventoryResult.Reject(Rejection);
        if (recipe.RequiredGrowthExperience > 0 && target.Core.Extension90 < recipe.RequiredGrowthExperience)
            return InventoryResult.Reject(Rejection);
        var sourceTemplate = items.Get(blueprintItemId);
        if (sourceTemplate.Id != blueprintItemId || !sourceTemplate.AllowsActivation(character.Level, character.Job) ||
            definition.TicketType == 7 && !character.IsPvpPrivate ||
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
        var core = instance.Core;
        if (definition.TicketType is 0 or 2 or >= 8 and <= 17 or 20 or 22)
        {
            // 低级勇者及魔法封印转圣物明确清除原打造，实例身份、品级与剩余期限仍沿用。
            core = InventoryRewardGrantService.Create(output, utcNow: now) with
            { Value = core.Value, ExpireTime = core.ExpireTime, SortLockFlag = core.SortLockFlag,
                DisjointForbidden = core.DisjointForbidden, DestructionSoulForbidden = core.DestructionSoulForbidden };
        }
        if (definition.TicketType is 1 or 4 or 6 or 18)
            core = core with { EmblemSocketCount = 0, Emblem1 = 0, Emblem2 = 0 };
        int maximum = core.AmplifyType == 0 ? definition.MaximumUpgrade : definition.MaximumAmplify;
        if (maximum >= 0)
            core = core with { Attr = (byte)((core.Attr & 0xE0) | Math.Min(core.Upgrade, maximum)) };
        if (definition.MaximumRefinement >= 0)
            core = core with { GenuineUpgrade = (byte)((core.GenuineUpgrade & 0x80) |
                Math.Min(core.GenuineUpgrade & 0x7F, definition.MaximumRefinement)) };
        if (definition.TicketType is >= 8 and <= 20 or 22)
            core = core with { SealFlag = 0 };
        var upgraded = instance with
        {
            Core = core with
            {
                ItemId = recipe.OutputItemId,
                EmancipateLevel = levelIncrease == 0 ? core.EmancipateLevel : unchecked((byte)((sbyte)core.EmancipateLevel + levelIncrease)),
                Extension90 = recipe.RequiredGrowthExperience > 0 ? (ushort)0 : core.Extension90,
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
        return new(true, 0, new(before, after, levelIncrease == 0 ? "upgrade_chronicle" : LevelReason,
            new(character, deadline, Clock, requirePosition: true)));
    }
}
