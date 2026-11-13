using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryEnchantmentService(ItemCatalog items, EnchantmentCatalog enchantments, TimeProvider? clock = null,
    Func<int, int>? random = null)
{
    // 272有公共u16拒绝分支；细分错误文案尚待客户端验收，不复用其他版本的宝珠错误表。
    internal const ushort Rejection = 23;
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;
    internal EnchantmentCatalog Catalog => enchantments;

    public InventoryResult Plan(Character character, EnchantmentRequest request, long utcSeconds)
    {
        var before = character.Inventory;
        var source = before.Find(request.Source);
        var target = before.Find(request.Target);
        if (request.Source.List != InventoryListType.Main || request.Target.List is not
            (InventoryListType.Main or InventoryListType.Equipment or InventoryListType.Pet) || request.Source == request.Target ||
            source is null || target is null || source.Uid <= 0 || target.Uid <= 0 || source.Core.Kind != ItemKind.Consumable ||
            !source.Core.IsStackable || target.Core.IsStackable || source.Core.EquipmentLockId != 0 || target.Core.EquipmentLockId != 0 ||
            source.IsExpired(utcSeconds) || target.IsExpired(utcSeconds))
            return InventoryResult.Reject(Rejection);
        var definition = Definition(source.Core.ItemId);
        return definition is null ? InventoryResult.Reject(Rejection) : Plan(character, request, utcSeconds, definition);
    }

    internal EnchantmentDefinition? Definition(int itemId)
    {
        try { return enchantments.Get(itemId); }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
        { return null; }
    }

    internal InventoryResult Plan(Character character, EnchantmentRequest request, long utcSeconds, EnchantmentDefinition definition)
    {
        var source = character.Inventory.Find(request.Source);
        var target = character.Inventory.Find(request.Target);
        if (source is null || target is null || definition.BeadItemId != source.Core.ItemId)
            return InventoryResult.Reject(Rejection);
        ItemTemplate beadTemplate, targetTemplate;
        try
        {
            if (definition.UsesInstanceUpgrade)
            {
                var resolved = enchantments.Get(source.Core.ItemId, source.Core.EnchantUpgrade);
                if (resolved is null) return InventoryResult.Reject(Rejection);
                definition = resolved;
            }
            beadTemplate = items.Get(source.Core.ItemId);
            targetTemplate = items.Get(target.Core.ItemId);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
        { return InventoryResult.Reject(Rejection); }
        // 业务规划与抽样在资源catch之外；程序、RNG及提交异常不得冒充资源拒绝。
        return Plan(character, request, utcSeconds, definition, beadTemplate, targetTemplate, random, Clock);
    }

    internal static InventoryResult Plan(Character character, EnchantmentRequest request, long now,
        EnchantmentDefinition definition, ItemTemplate beadTemplate, ItemTemplate targetTemplate, Func<int, int>? random = null, TimeProvider? clock = null)
    {
        var before = character.Inventory;
        var source = before.Find(request.Source);
        var target = before.Find(request.Target);
        if (request.Source.List != InventoryListType.Main || request.Target.List is not
            (InventoryListType.Main or InventoryListType.Equipment or InventoryListType.Pet) || request.Source == request.Target ||
            source is null || target is null || source.Uid <= 0 || target.Uid <= 0 || source.Core.Kind != ItemKind.Consumable ||
            source.Core.Count <= 0 || target.Core.IsStackable || source.Core.EquipmentLockId != 0 || target.Core.EquipmentLockId != 0 ||
            source.IsExpired(now) || target.IsExpired(now) || beadTemplate.Id != source.Core.ItemId || beadTemplate.Kind != source.Core.Kind ||
            targetTemplate.Id != target.Core.ItemId || targetTemplate.Kind != target.Core.Kind || definition.BeadItemId != source.Core.ItemId ||
            definition.CardItemId <= 0 && definition.RandomTitle is null && definition.RandomCards is null ||
            !definition.AllowsTarget(targetTemplate) ||
            !ItemSlotBoundService.Allows(before, source.Position, source.Core.Kind) ||
            !ItemSlotBoundService.Allows(before, target.Position, target.Core.Kind) || beadTemplate.UseForbidden ||
            character.Level < beadTemplate.MinimumLevel ||
            !InventoryMoveService.RequiredSkillSatisfied(character, beadTemplate))
            return InventoryResult.Reject(Rejection);
        var consumed = InventoryDeleteService.Plan(before, source.Position, source.Core.ItemId, 1);
        if (!consumed.Accepted) return InventoryResult.Reject(Rejection);
        // 随机技能独立于普通卡片附魔；低16位奖池、高16位序号由客户端按完整表解释。
        var core = definition.RandomTitle is { } pool ? target.Core with { Extension97 = pool.Draw(random ?? Random.Shared.Next) } :
            target.Core with { EnchantCardId = definition.RandomCards?.Draw(random ?? Random.Shared.Next) ?? definition.CardItemId,
                EnchantUpgrade = definition.Upgrade };
        var changed = target with { Core = core };
        var after = consumed.Plan!.After with { Items = consumed.Plan.After.Items.Replace(target, changed) };
        after.Validate();
        long? deadline = source.ExpireTime > 0 ? source.ExpireTime : null;
        if (target.ExpireTime > 0) deadline = deadline is null ? target.ExpireTime : Math.Min(deadline.Value, target.ExpireTime);
        var time = clock ?? TimeProvider.System;
        return new(true, 0, new InventoryPlan(before, after, "enchant_by_bead", new(character, deadline, clock: clock, requirePosition: true))
        {
            EquipmentEnchant = EquipmentEnchantFact.Capture(before, consumed.Plan.After, after, source, target,
                definition.RandomTitle is null ? EquipmentEnchantMode.BeadCard : EquipmentEnchantMode.BeadTitle,
                "enchant_by_bead", time.GetUtcNow(), time)
        });
    }
}
