using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Characters;

internal sealed record PermanentGrowthPlan(Character Expected, InventoryItem Source, PermanentGrowthDefinition Definition,
    InventoryPlan Inventory, PermanentStatBonuses Bonuses, CharacterLearningState Learning);

internal sealed class PermanentGrowthService(ItemCatalog items, CharacterStatComputer stats, SkillLearningCatalog skills,
    TimeProvider? clock = null)
{
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public PermanentGrowthPlan Prepare(Character expected, InventorySlot slot)
    {
        var source = expected.Inventory.Find(slot);
        if (slot.List != InventoryListType.Main || source is null || source.Uid <= 0 || source.Core.Count <= 0 ||
            source.Core.Kind != ItemKind.Consumable || source.Core.EquipmentLockId != 0 ||
            source.IsExpired(Clock.GetUtcNow().ToUnixTimeSeconds()) ||
            !ItemSlotBoundService.Allows(expected.Inventory, slot, ItemKind.Consumable) ||
            expected.DungeonState is null || !expected.Learning.Initialized)
            throw new InvalidDataException("永久成长物品源实例或角色附属状态无效");
        var definition = items.PermanentGrowth(source.Core.ItemId);
        if (definition is null || !definition.Item.AllowsActivation(expected.Level, expected.Job))
            throw new InvalidDataException("永久成长物品资源或等级资格不符");
        var (bonuses, learning) = Apply(expected, definition);
        // 不能让当前合法的属性石在日后升级或转职时才造成 Player 字段溢出。
        stats.ValidatePermanentBonuses(expected.Job, bonuses);
        if (learning.BonusSp is int sp && checked(skills.TotalSp(CharacterGrowthCatalog.MaximumSupportedLevel) + sp) > ushort.MaxValue ||
            learning.BonusTp is int tp && checked(skills.TotalTp(CharacterGrowthCatalog.MaximumSupportedLevel) + tp) > ushort.MaxValue)
            throw new InvalidDataException("额外技能点超过完整成长预算范围");
        var deletion = InventoryDeleteService.Plan(expected.Inventory, slot, source.Core.Value, 1);
        if (!deletion.Accepted) throw new InvalidDataException("永久成长物品不能消费一件");
        return new(expected, source, definition,
            new(expected.Inventory, deletion.Plan!.After, "use_permanent_growth", new(expected)), bonuses, learning);
    }

    internal static (PermanentStatBonuses Bonuses, CharacterLearningState Learning) Apply(Character expected, PermanentGrowthDefinition definition)
    {
        expected.PermanentStats.Validate();
        expected.Learning.Validate();
        if (definition.Amount <= 0 || !expected.Learning.Initialized)
            throw new InvalidDataException("永久成长增量或技能来源未初始化");
        var learning = expected.Learning;
        if (definition.Kind is PermanentGrowthKind.Sp or PermanentGrowthKind.Tp)
        {
            bool sp = definition.Kind == PermanentGrowthKind.Sp;
            int before = (sp ? learning.BonusSp : learning.BonusTp) ??
                throw new InvalidDataException("额外点数来源未知，不能从当前余额反推");
            int bonus = checked(before + definition.Amount);
            learning = learning with
            {
                BonusSp = sp ? bonus : learning.BonusSp, BonusTp = sp ? learning.BonusTp : bonus,
                Trees = Array.AsReadOnly(learning.Trees.Select(tree => sp
                    ? tree with { Sp = checked((ushort)(tree.Sp + definition.Amount)) }
                    : tree with { Tp = checked((ushort)(tree.Tp + definition.Amount)) }).ToArray())
            };
            learning.Validate();
            return (expected.PermanentStats, learning);
        }
        return (expected.PermanentStats.Add(definition.Kind, definition.Amount), learning);
    }
}
