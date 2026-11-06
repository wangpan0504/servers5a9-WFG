using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryPremiumContractUseService(ItemCatalog items, PremiumContractCatalog contracts, TimeProvider? time = null)
{
    public TimeProvider Clock { get; } = time ?? TimeProvider.System;
    public bool Recognizes(int itemId)
    {
        if (contracts.Recognizes(itemId)) return true;
        try { return items.Get(itemId).IsPremiumService; }
        catch (InvalidDataException) { return false; }
    }

    public bool TryDefinition(int itemId, out PremiumContract definition) => contracts.TryConsumableContract(itemId, out definition);

    public InventoryResult Plan(Character character, ConsumableUseRequest request, long now)
    {
        if (!TryDefinition(request.ItemId, out _)) return InventoryResult.Reject(123);
        return Plan(character, request, now, contracts, items.Get(request.ItemId), Clock);
    }

    internal static InventoryResult Plan(Character character, ConsumableUseRequest request, long now,
        PremiumContractCatalog contracts, ItemTemplate template, TimeProvider? clock = null)
    {
        if (!contracts.TryConsumableContract(request.ItemId, out var contract)) return InventoryResult.Reject(123);
        var before = character.Inventory;
        var source = before.Find(request.Position);
        if (request.Argument != 0 || request.Position.List != InventoryListType.Main ||
            source is null || source.Uid <= 0 || source.Core.ItemId != request.ItemId || source.Core.Kind != ItemKind.Consumable ||
            source.Core.Count <= 0 || !ItemSlotBoundService.Allows(before, request.Position, ItemKind.Consumable) ||
            source.Core.EquipmentLockId != 0 || source.Core.IsExpired(now) ||
            contract.ItemId != request.ItemId || contract.DurationSeconds <= 0 ||
            template.Id != request.ItemId || template.Kind != ItemKind.Consumable || !template.AllowsActivation(character.Level, character.Job) ||
            !InventoryMoveService.RequiredSkillSatisfied(character, template))
            return InventoryResult.Reject(123);

        var deletion = InventoryDeleteService.Plan(before, request.Position, request.ItemId, 1);
        if (!deletion.Accepted || deletion.Plan is null) return InventoryResult.Reject(123);
        try
        {
            // 实体扣一件和账号续期构成同一个规划；提交、owner和重复请求由 Session 统一控制。
            var wallet = before.AccountWallet.ActivateContract(contract.Type, contract.DurationSeconds, DateTimeOffset.FromUnixTimeSeconds(now));
            var after = deletion.Plan.After with { AccountWallet = wallet };
            return new(true, 0, new(before, after, "use_premium_contract", new(character, source.ExpireTime > 0 ? source.ExpireTime : null, clock)));
        }
        catch (Exception error) when (error is OverflowException or ArgumentOutOfRangeException)
        {
            return InventoryResult.Reject(123);
        }
    }
}
