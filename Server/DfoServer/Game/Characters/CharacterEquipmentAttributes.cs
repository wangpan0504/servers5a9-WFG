using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Characters;

internal enum EquipmentQualityComponent
{
    PhysicalAttack, MagicalAttack, PhysicalDefense, MagicalDefense
}

internal enum EquipmentAttributeUnsupported
{
    None, CharacterScope, UncommittedItem, TemplateScope, SlotOrQualification,
    ResourceShape, ResourceCorrection, BrokenEquipment, CoreCorrection, StrengtheningOrLevelCorrection
}

// 数字只对应资源equipment字段的品质阶段，不能作为面板或战斗最终总量。
internal sealed record CharacterEquipmentContribution(long Uid, InventorySlot Slot, ItemCore Core,
    string ScriptPath, int? AdjustedGrade, ImmutableDictionary<EquipmentQualityComponent, int> QualityParts,
    EquipmentAttributeUnsupported Unsupported, IReadOnlyDictionary<string, int> DeclaredScalars,
    EquipmentAttributePair? DeclaredSeparateAttack)
{
    // 保留separate attack两项原序；这是已证普通品质阶段，不是完整面板或战斗总量。
    public EquipmentAttributePair? QualitySeparateAttack { get; init; }
}

internal sealed record CharacterEquipmentAttributeResult(ushort CharacterId, long Revision, byte Job, byte GrowType,
    byte Level, byte SkillContext, ImmutableArray<LearnedSkill> Skills, ImmutableArray<CharacterEquipmentContribution> Equipment);

internal static class CharacterEquipmentAttributes
{
    private static readonly ImmutableDictionary<int, string> SupportedTemplates = new Dictionary<int, string>
    {
        [101010912] = "equipment/character/swordman/weapon/katana/101010912.equ",
        [10400] = "equipment/character/common/jacket/leather/vest_crleather.equ",
        [12400] = "equipment/character/common/pants/leather/pant_crleather.equ",
        [16400] = "equipment/character/common/belt/leather/str_crleather.equ",
        [18400] = "equipment/character/common/shoes/leather/slp_crleather.equ",
        [101010438] = "equipment/character/swordman/weapon/katana/101010438.equ",
        [10403] = "equipment/character/common/jacket/leather/vest_leather.equ",
        [12403] = "equipment/character/common/pants/leather/pant_leather.equ",
        [14403] = "equipment/character/common/shoulder/leather/shd_leather.equ",
        [16403] = "equipment/character/common/belt/leather/str_leather.equ",
        [18403] = "equipment/character/common/shoes/leather/slp_leather.equ"
    }.ToImmutableDictionary();
    private static readonly (string Tag, EquipmentQualityComponent Component)[] QualityFields =
    [
        ("equipment physical attack", EquipmentQualityComponent.PhysicalAttack),
        ("equipment magical attack", EquipmentQualityComponent.MagicalAttack),
        ("equipment physical defense", EquipmentQualityComponent.PhysicalDefense),
        ("equipment magical defense", EquipmentQualityComponent.MagicalDefense)
    ];

    // 解析和评估在不可变Selected快照上锁外执行；发布资格仍归Session提交回执。
    public static CharacterEquipmentAttributeResult Evaluate(Character character, byte skillContext,
        ItemCatalog items, EquipmentCatalog equipment)
    {
        if (skillContext > 1)
            throw new ArgumentOutOfRangeException(nameof(skillContext));
        var skills = character.Learning.Initialized
            ? character.Learning.Trees.Single(tree => tree.Context == skillContext).Skills.ToImmutableArray()
            : character.Skills.Select((skill, index) => new LearnedSkill(checked((byte)index), skill.Id, skill.Level)).ToImmutableArray();
        var contributions = character.Inventory.Items.Where(item => item.Position.List == InventoryListType.Equipment)
            .OrderBy(item => item.Position.Index).Select(item =>
            {
                if (!SupportedTemplates.ContainsKey(item.Core.ItemId))
                    return Unsupported(item, EquipmentAttributeUnsupported.TemplateScope);
                return EvaluateItem(character, item, items.Get(item.Core.ItemId), equipment.GetAttributes(item.Core.ItemId));
            }).ToImmutableArray();
        return new(character.Id, character.Revision, character.Job, character.GrowType, character.Level,
            skillContext, skills, contributions);
    }

    internal static CharacterEquipmentContribution EvaluateItem(Character character, InventoryItem item,
        ItemTemplate template, EquipmentAttributeDefinition declaration)
    {
        if (character.Job != 0 || character.GrowType != 0 || character.Level is < 1 or > 95)
            return Unsupported(item, EquipmentAttributeUnsupported.CharacterScope, declaration);
        if (item.Uid <= 0)
            return Unsupported(item, EquipmentAttributeUnsupported.UncommittedItem, declaration);
        if (!SupportedTemplates.TryGetValue(item.Core.ItemId, out string? expectedPath) || template.Id != item.Core.ItemId ||
            declaration.TemplateId != template.Id || declaration.ScriptPath != expectedPath)
            return Unsupported(item, EquipmentAttributeUnsupported.TemplateScope, declaration);
        if (item.Position.List != InventoryListType.Equipment || item.Position.Index != template.EquipmentSlot ||
            template.EquipmentSlot is not (12 or 14 or 15 or 16 or 17 or 18) || template.Kind != ItemKind.Equipment ||
            (template.Jobs & 1) == 0 || character.Level < template.MinimumLevel ||
            template.RequiredSkill != 0 && !character.Skills.Any(skill => skill.Id == template.RequiredSkill && skill.Level > 0))
            return Unsupported(item, EquipmentAttributeUnsupported.SlotOrQualification, declaration);
        if (!declaration.Scalars.TryGetValue("grade", out int grade) || !declaration.Scalars.TryGetValue("rarity", out int rarity) ||
            grade != template.Grade || rarity != template.Rarity || rarity is not (0 or 1) ||
            !declaration.Scalars.TryGetValue("minimum level", out int minimum) || minimum != template.MinimumLevel ||
            template.Expiration.Days != 0 || template.Expiration.AbsoluteTime != 0)
            return Unsupported(item, EquipmentAttributeUnsupported.ResourceShape, declaration);
        int adjustedGrade = EquipmentAttributeRules.GetNormalAdjustedGrade(grade, rarity, item.Core.Attr, item.Core.EmancipateLevel);
        if (declaration.NoRandom || declaration.HasConditionalRules || declaration.HasLevelSectionAbility || declaration.HasImport ||
            declaration.HasSeparateRate)
            return Unsupported(item, EquipmentAttributeUnsupported.ResourceCorrection, declaration, adjustedGrade);
        if (item.Core.Durability == 0 || item.Core.Durability > template.Durability)
            return Unsupported(item, EquipmentAttributeUnsupported.BrokenEquipment, declaration, adjustedGrade);

        // 发放策略提供完整默认源；耐久及安全/排序锁、封装/交易状态不是此品质阶段的加成。
        var expected = InventoryRewardGrantService.Create(template, utcNow: DateTimeOffset.UnixEpoch) with
        {
            Durability = item.Core.Durability, SortLockFlag = item.Core.SortLockFlag,
            EquipmentLockId = item.Core.EquipmentLockId, SealFlag = item.Core.SealFlag,
            TradeRestriction = item.Core.TradeRestriction, Attr = item.Core.Attr, EmancipateLevel = item.Core.EmancipateLevel
        };
        if (item.Core != expected || item.Avatar is not null || item.Creature is not null)
            return Unsupported(item, EquipmentAttributeUnsupported.CoreCorrection, declaration, adjustedGrade);
        if (item.Core.Attr != 0 || item.Core.EmancipateLevel != 0)
            return Unsupported(item, EquipmentAttributeUnsupported.StrengtheningOrLevelCorrection, declaration, adjustedGrade);

        var values = ImmutableDictionary.CreateBuilder<EquipmentQualityComponent, int>();
        foreach (var field in QualityFields)
        {
            if (!declaration.Pairs.TryGetValue(field.Tag, out var basis))
                continue; // 这里只输出已声明的分量，不以缺项伪造最终装备总值。
            var merged = declaration.Pairs.TryGetValue("add " + field.Tag, out var addition)
                ? EquipmentAttributeRules.AddDeclaration(basis, addition) : basis;
            if (field.Component == EquipmentQualityComponent.MagicalDefense && merged.First != 0)
                return Unsupported(item, EquipmentAttributeUnsupported.ResourceCorrection, declaration, adjustedGrade);
            // normal调用02909240(index0)，两项不是通用的品质上下端点。
            values.Add(field.Component, EquipmentAttributeRules.GetNormalDefaultQualityValue(merged.First, item.Core.Value));
        }
        var separate = declaration.Pairs.GetValueOrDefault("separate attack");
        return new(item.Uid, item.Position, item.Core, declaration.ScriptPath, adjustedGrade,
            values.ToImmutable(), EquipmentAttributeUnsupported.None, declaration.Scalars,
            separate)
        {
            QualitySeparateAttack = separate is null ? null :
                EquipmentAttributeRules.GetNormalDefaultSeparateAttackQuality(separate, item.Core.Value)
        };
    }

    private static CharacterEquipmentContribution Unsupported(InventoryItem item, EquipmentAttributeUnsupported reason,
        EquipmentAttributeDefinition? declaration = null, int? adjustedGrade = null) =>
        new(item.Uid, item.Position, item.Core, declaration?.ScriptPath ?? "", adjustedGrade,
            ImmutableDictionary<EquipmentQualityComponent, int>.Empty, reason,
            declaration?.Scalars ?? ImmutableDictionary<string, int>.Empty,
            declaration?.Pairs.GetValueOrDefault("separate attack"));
}

internal sealed record CharacterBaselineAttributeProjection(ushort CharacterId, long Revision, byte Job, byte GrowType,
    byte Level, CharacterStats Stats);

internal static class CharacterBaselineAttributes
{
    public static bool TryEvaluate(Character character, CharacterCatalog characters,
        [NotNullWhen(true)] out CharacterBaselineAttributeProjection? projection)
    {
        projection = null;
        // 创建用例明确拥有副本附属状态与完整两树；存量无owner的角色维持原投影。
        if (character.Job != 0 || character.GrowType != 0 || character.Level is < 1 or > 95 ||
            character.DungeonState is null || !character.Learning.Initialized)
            return false;
        character.DungeonState.Validate();
        character.Learning.Validate();
        // 当前普通角色源仅有CHR基础成长；服务未接入永久属性授予。
        // 装备、511精通和174倍率由完整Core/技能树在native派生buffer中应用。
        projection = new(character.Id, character.Revision, character.Job, character.GrowType, character.Level,
            new CharacterStatComputer(characters).ComputeLevelBaseStats(character.Job, character.GrowType, character.Level));
        return true;
    }
}
