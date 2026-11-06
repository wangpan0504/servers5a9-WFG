using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryChronicleEnchantService(ItemCatalog items, ChronicleEnchantCatalog chronicles,
    DisjointCatalog disjoint, AmplifyUpgradeCatalog upgrades, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces,
    Random? random = null, TimeProvider? clock = null)
{
    internal const ushort Rejection = 17;
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public InventoryResult Plan(Character character, ChronicleEnchantRequest request, out ChronicleEnchantReceipt? receipt)
    {
        receipt = null;
        var source = character.Inventory.Find(new(InventoryListType.Main, request.MaterialSlot));
        var target = character.Inventory.Find(request.Target);
        if (source is null || target is null || source.Core.ItemId != request.MaterialItemId || target.Core.ItemId != request.TargetItemId)
            return InventoryResult.Reject(4);
        var definition = chronicles.Get(source.Core.ItemId);
        if (definition is null) return InventoryResult.Reject(Rejection);
        var sourceTemplate = items.Get(source.Core.ItemId);
        var template = items.Get(target.Core.ItemId);
        var action = items.StackableAction(source.Core.ItemId);
        if (action is { } identity && identity.ClientType != 3) return InventoryResult.Reject(Rejection);
        long now = Clock.GetUtcNow().ToUnixTimeSeconds();
        var rng = random ?? Random.Shared;
        return Plan(character, request, now, definition, sourceTemplate, template,
            chronicles.Allows(template.Id, character.Job, character.GrowType, template.EquipmentSlot, definition, request.Number),
            () => rng.Next(101), paid => Destroy(character, paid, target, template, rng, now), out receipt, Clock);
    }

    internal static InventoryResult Plan(Character character, ChronicleEnchantRequest request, long now,
        ChronicleEnchantDefinition definition, ItemTemplate sourceTemplate, ItemTemplate template, bool matchesCharacter,
        Func<int> roll, Func<InventorySnapshot, ChronicleDestruction?> destroy, out ChronicleEnchantReceipt? receipt, TimeProvider? clock = null)
    {
        receipt = null;
        var before = character.Inventory;
        var sourcePosition = new InventorySlot(InventoryListType.Main, request.MaterialSlot);
        var source = before.Find(sourcePosition);
        var target = before.Find(request.Target);
        if (request.Target.List != InventoryListType.Main || sourcePosition == request.Target || source is null || target is null ||
            source.Uid <= 0 || target.Uid <= 0 || source.Uid == target.Uid || source.Core.ItemId != request.MaterialItemId ||
            target.Core.ItemId != request.TargetItemId || !source.Core.IsStackable || source.Core.Count is <= 0 or > ushort.MaxValue ||
            target.Core.Kind != ItemKind.Equipment || target.Core.IsRental || source.IsExpired(now) || target.IsExpired(now) ||
            sourceTemplate.Id != source.Core.ItemId || sourceTemplate.Kind != source.Core.Kind || template.Id != target.Core.ItemId ||
            template.Kind != target.Core.Kind || !sourceTemplate.AllowsActivation(character.Level, character.Job) ||
            !InventoryMoveService.RequiredSkillSatisfied(character, sourceTemplate) ||
            !ItemSlotBoundService.Allows(before, source.Position, source.Core.Kind) || !ItemSlotBoundService.Allows(before, target.Position, target.Core.Kind))
            return InventoryResult.Reject(Rejection);
        if (source.Core.EquipmentLockId != 0 || target.Core.EquipmentLockId != 0) return InventoryResult.Reject(214);
        if ((target.Core.AmplifyType & 0x80) != 0 || target.Core.Durability != template.Durability || !matchesCharacter ||
            target.Core.Chronicle1 != default || target.Core.Chronicle0 is { Id: not 0 } previous &&
                previous.Id == definition.EffectItemId && previous.Number == request.Number)
            return InventoryResult.Reject(19);
        if (definition.Type > 3 || definition.FirstChance is < 0 or > 100 || definition.SecondChance is < 0 or > 100)
            return InventoryResult.Reject(Rejection);
        var paid = InventoryDeleteService.Plan(before, sourcePosition, source.Core.ItemId, 1);
        if (!paid.Accepted) return InventoryResult.Reject(Rejection);
        int chance = target.Core.Chronicle0 == default ? definition.FirstChance : definition.SecondChance;
        // 在抽结果前冻结完整失败返还与容量，不能靠失败后满包拒绝免费重抽。
        var destroyed = chance < 100 ? destroy(paid.Plan!.After) : null;
        if (chance < 100 && destroyed is null) return InventoryResult.Reject(21);
        int value = roll();
        if (value is < 0 or > 100) throw new InvalidOperationException("气息随机源越界");
        // df_game_r的randInt(100)取0..100，成功判断是<=，不是另外改成百分之一百域。
        bool succeeded = value <= chance;
        InventorySnapshot after;
        if (succeeded)
        {
            var option = new ChronicleOption(definition.EffectItemId, character.Job, (byte)(character.GrowType & 0x0F),
                template.EquipmentSlot, request.Number);
            var core = target.Core.Chronicle0 == default ? target.Core with { Chronicle0 = option } : target.Core with { Chronicle1 = option };
            after = paid.Plan!.After with { Items = paid.Plan.After.Items.Replace(target, target with { Core = core }) };
        }
        else after = destroyed!.After;
        after.Validate(allocating: true);
        long? deadline = InventoryAvatarPackageService.EarliestDeadline(source, [target]);
        foreach (var item in after.Items)
            if (item.ExpireTime > 0 && before.Find(item.Position) != item &&
                (deadline is null || item.ExpireTime < deadline)) deadline = item.ExpireTime;
        receipt = new(request.MaterialSlot, checked((ushort)(source.Core.Count - 1)), request.Target.Index, succeeded,
            succeeded ? [] : destroyed!.Refunds);
        return new(true, 0, new(before, after, "chronicle_enchant", new(character, deadline, clock)));
    }

    ChronicleDestruction? Destroy(Character character, InventorySnapshot paid, InventoryItem target, ItemTemplate template, Random rng, long now)
    {
        var rewards = DisjointResultCalculator.Calculate(disjoint, template, rng,
            grantSouls: !target.Core.DestructionSoulForbidden).ToList();
        if (rewards.Count == 0) throw new InvalidDataException("气息破坏返还资源尚未完整支持");
        foreach (var bonus in target.Core.AmplifyType == 0 ? new[] { upgrades.CompanionBonus } : new[] { upgrades.Rules.Bonus, upgrades.CompanionBonus })
            if (bonus.Count(template, target.Core.Upgrade) is > 0 and var count) rewards.Add(new(bonus.ItemId, count));
        var released = paid with { Items = paid.Items.Remove(target) };
        foreach (var group in rewards.GroupBy(row => row.ItemId))
            if (CurrencyWallet.RewardKind(group.Key) is { } currency && CurrencyWallet.Get(released, currency) + group.Sum(row => (long)row.Count) > int.MaxValue)
                return null;
        var grant = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = released }, rewards, items, goldLimits, pieces,
            DateTimeOffset.FromUnixTimeSeconds(now));
        if (!grant.Accepted) return null;
        var after = grant.Plan!.After;
        var refunds = ImmutableArray.CreateBuilder<EnhancementRefund>();
        foreach (int id in rewards.Select(row => row.ItemId).Distinct())
            if (CurrencyWallet.RewardKind(id) is { } currency)
                refunds.Add(new(ItemSlotBoundService.CurrencySlot(currency) ?? throw new InvalidDataException("气息返还缺少钱包槽"), id,
                    checked((int)(CurrencyWallet.Get(after, currency) - CurrencyWallet.Get(released, currency)))));
            else foreach (var item in after.Items.Where(item => item.Position.List == InventoryListType.Main && item.Core.ItemId == id &&
                item.Core.Count > (released.Find(item.Position)?.Core.Count ?? 0)))
                refunds.Add(new(item.Position.Index, id, item.Core.Count - (released.Find(item.Position)?.Core.Count ?? 0)));
        return refunds.Count <= byte.MaxValue ? new(after, refunds.ToImmutable()) : null;
    }
}
