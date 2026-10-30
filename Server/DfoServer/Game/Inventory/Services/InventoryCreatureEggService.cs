using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryCreatureEggService(CreatureEggCatalog eggs, ItemCatalog items, TimeProvider? clock = null)
{
    // 102失败结构已核对为公共u16；具体文案编号未确认，不沿用其它命令的错误表。
    internal const ushort Rejection = 0;
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public InventoryResult Plan(Character character, InventorySlot position, long utcSeconds)
    {
        var source = character.Inventory.Find(position);
        if (position.List != InventoryListType.Pet || position.Index is < 0 or > 139 || source is null)
            return InventoryResult.Reject(Rejection);
        var definition = eggs.Get(source.Core.ItemId);
        return definition is null ? InventoryResult.Reject(Rejection) : Plan(character, position, utcSeconds, definition, items.Get(source.Core.ItemId));
    }

    internal static InventoryResult Plan(Character character, InventorySlot position, long utcSeconds, CreatureEggDefinition definition,
        ItemTemplate template)
    {
        var before = character.Inventory;
        var source = before.Find(position);
        if (position.List != InventoryListType.Pet || position.Index is < 0 or > 139 || source is null ||
            source.Uid <= 0 || source.Uid > int.MaxValue || source.Core.Value != source.Uid || source.Core.Kind != ItemKind.Creature ||
            source.Creature is null || source.Avatar is not null || source.Core.ExpireTime != 0 || source.Core.EquipmentLockId != 0 ||
            source.IsExpired(utcSeconds) || source.Core.ItemId != definition.EggItemId ||
            definition.EggItemId <= 0 || definition.OutputItemId <= 0 || definition.EggItemId == definition.OutputItemId || definition.Species < 0 || definition.OutputMode > 1 ||
            template.Id != source.Core.ItemId || template.Kind != ItemKind.Creature || template.EquipmentSlot != 26 ||
            template.UseForbidden ||
            !ItemSlotBoundService.Allows(before, source.Position, source.Core.Kind))
            return InventoryResult.Reject(Rejection);
        source.ValidateDetails(allocating: false);
        // 宠物模板的等级不能直接当作角色使用等级；成长资格不借普通装备穿戴规则推定。
        // 蛋是已拥有的独立宠物实例：只转换模板，完整UID、详情、实例期限和其它核心保持原事实。
        var detail = source.Creature;
        if (detail.Name.Length == 0)
            detail = detail with { Name = DfoServer.Game.Creatures.CreatureRules.DefaultName(definition.DefaultName) };
        if (definition.OutputMode == 1 && detail.Mode == 0)
            detail = detail with { Mode = 1, Mode1Field0 = 10, Mode1Field1 = 5 };
        var hatched = source with { Core = source.Core with { ItemId = definition.OutputItemId, SealFlag = 0 }, Creature = detail };
        var after = before with { Items = before.Items.Replace(source, hatched) };
        return new(true, 0, new(before, after, "hatch_creature_egg",
            new(character, source.ExpireTime == 0 ? null : source.ExpireTime)));
    }
}
