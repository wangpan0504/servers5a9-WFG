using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed record EquipmentTransportPlan(Character Character, CharacterRosterEntry? Recipient,
    InventoryPlan Inventory, InventoryItem Target, InventoryItem Transferred, long Created);
internal interface IEquipmentTransportStore
{
    CreatureTransportCommit CommitEquipmentTransport(AccountOwners.Lease owner, EquipmentTransportPlan plan);
}

internal sealed class EquipmentTransportService(Func<int, ItemTemplate> items, Func<int, ActionConsumableDefinition?> definitions, EquipmentTransportCatalog catalog,
    EpicPieceCatalog pieces, IEquipmentTransportStore store, TimeProvider? time = null)
{
    readonly TimeProvider clock = time ?? TimeProvider.System;

    public ushort Plan(Character character, CharacterRosterEntry? recipient, CreatureTransportRequest request,
        string place, out EquipmentTransportPlan? plan)
    {
        plan = null;
        if (request.Mode is not (0 or 2 or 7 or 51 or 100) || request.StoneSlot > short.MaxValue || request.PetSlot > short.MaxValue ||
            request.Mode != 0 && (recipient is null || recipient.Id == character.Id || recipient.Id == 0) ||
            request.Mode == 2 && request.StoneSlot != 0) return 7;
        var targetSlot = new InventorySlot(request.Mode switch { 7 => InventoryListType.Avatar, 100 => InventoryListType.GuildMedal, _ => InventoryListType.Main }, (short)request.PetSlot);
        var sourceSlot = new InventorySlot(InventoryListType.Main, (short)request.StoneSlot);
        var before = character.Inventory;
        var target = before.Find(targetSlot);
        var source = request.Mode == 2 ? null : before.Find(sourceSlot);
        var now = clock.GetUtcNow();
        if (target is not { Uid: > 0 } || targetSlot == sourceSlot || target.Core.IsRental || target.IsExpired(now.ToUnixTimeSeconds()) ||
            !ItemSlotBoundService.Allows(before, targetSlot, target.Core.Kind)) return 7;
        if (target.Core.EquipmentLockId != 0 || target.Core.SortLockFlag != 0 || source is { Core.EquipmentLockId: not 0 }) return 214;
        var template = items(target.Core.ItemId);
        var expectedKind = request.Mode switch { 7 => ItemKind.Avatar, 51 => ItemKind.Emblem, 100 => ItemKind.GuildMedal, _ => ItemKind.Equipment };
        if (template.Kind != expectedKind || target.Core.Kind != expectedKind || template.PvpFreeEquipment ||
            target.Core.TradeRestriction == 1 || target.Core.RandomChange.Type != 0) return 7;
        if (request.Mode == 100 && (recipient!.Job >= 16 || (template.Jobs & (1 << recipient.Job)) == 0)) return 7;
        ActionConsumableDefinition? action = source is null ? null : definitions(source.Core.ItemId);
        string label = request.Mode switch { 7 => "[avatar transport stone]", 51 => "[emblem transport stone]", 100 => "[flag transport stone]", _ => "[cargo transport stone]" };
        int type;
        if (request.Mode == 2) type = 7;
        else if (action is not { Parameters: [>= 0] } || action.Label != label) return 7;
        else type = action.Parameters[0];
        if (!catalog.Rules.TryGetValue(type, out var rule)) return 7;
        int offset = request.Mode == 2 ? unchecked((sbyte)target.Core.EmancipateLevel) : 0;
        int count = rule.StoneCost(template, offset);
        if (count <= 0) return 7;
        if (rule.ExcludedProperties.Contains("periodEquipment") && target.ExpireTime > 0) return 7;
        InventorySnapshot after;
        if (request.Mode == 2)
        {
            var costs = rule.NpcCosts(template, pieces, offset);
            if (costs is null) return 7;
            var paid = InventoryMaterialConsumptionService.Plan(before, costs, pieces, now);
            if (!paid.Accepted) return 21;
            after = paid.Plan!.After;
        }
        else
        {
            var paid = InventoryActionConsumableService.Plan(character, sourceSlot, action!, place, clock, now, count);
            if (!paid.Accepted) return 21;
            after = paid.Plan!.After;
        }
        // 艾肯只解除套装，单件词条、品级、强化和其他核心属性保持原实例。
        var core = target.Core;
        // 特效符文只豁免跨界石转入账号仓库；其他跨角色转移移除特效。
        if (request.Mode != 0 && core.Kind == ItemKind.Equipment) core = core with { Rune = 0 };
        if (template.Echon is not null)
            core = (EchonEquipmentState.Read(core) with { SetIndex = 0 }).Apply(core) with { EchonSet = null };
        var transferred = target with { Core = core };
        if (request.Mode == 0)
        {
            if (template.AccountCargoForbidden || before.AccountCargo.Capacity == 0) return 19;
            short destination = -1;
            for (short slot = 0; slot < before.AccountCargo.Capacity; slot++)
                if (before.Find(new(InventoryListType.AccountCargo, slot)) is null) { destination = slot; break; }
            if (destination < 0) return 19;
            transferred = transferred with { Position = new(InventoryListType.AccountCargo, destination) };
            after = after with { Items = after.Items.Replace(target, transferred) };
        }
        else if (request.Mode == 51 && target.Core.Count > 1)
        {
            transferred = transferred with { Uid = 0, Core = core with { Value = 1 } };
            after = after with { Items = after.Items.Replace(target, target with { Core = target.Core with { Value = target.Core.Count - 1 } }) };
        }
        else after = after with { Items = after.Items.Remove(target) };
        // 材料可能跨堆扣除，守住所有实际变化实例的期限和日切资格。
        long? deadline = before.Items.Where(item => item.ExpireTime > 0 && (item == target || item == source || after.Find(item.Position) != item))
            .Select(item => (long?)item.ExpireTime).Min();
        if (action is { DailyLimit: > 0 }) deadline = Math.Min(deadline ?? long.MaxValue, DailyBoosterUsage.NextReset(now).ToUnixTimeSeconds());
        plan = new(character, request.Mode == 0 ? null : recipient, new(before, after, "equipment_transport",
            new(character, deadline, clock, requirePosition: true)), target, transferred, now.ToUnixTimeSeconds());
        return 0;
    }

    public CreatureTransportCommit Commit(AccountOwners.Lease owner, EquipmentTransportPlan plan) => store.CommitEquipmentTransport(owner, plan);
}
