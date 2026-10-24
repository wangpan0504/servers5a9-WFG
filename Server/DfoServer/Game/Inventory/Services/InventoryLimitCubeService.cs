using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryLimitCubeService(ItemCatalog items, LimitCubeCatalog cubes, EpicPieceCatalog pieces,
    PremiumContractCatalog contracts, IReadOnlySet<int>? directActivationItems = null,
    Func<long, long>? random = null, GameDebugSettings? debugSettings = null,
    IndependentAttributePairCatalog? attributePairs = null)
{
    const ushort Rejection = 1;
    readonly Func<long, long> draw = random ?? Random.Shared.NextInt64;

    public InventoryResult Apply(Character character, LimitCubeRequest request,
        out LimitCubeReceipt? receipt, out string? rejection, DateTimeOffset? utcNow = null)
    {
        receipt = null;
        rejection = null;
        bool ignoreAbsoluteExpiration = debugSettings?.IgnoreCashShopAbsoluteExpiration == true;
        bool ignoreUsablePeriod = debugSettings?.IgnoreCashShopUsablePeriod == true;
        var now = utcNow ?? DateTimeOffset.UtcNow;
        long seconds = now.ToUnixTimeSeconds();
        var before = character.Inventory;
        if (seconds <= 0 || request.TargetItemId <= 0 || request.TargetSlot < 9 ||
            request.TicketSlot < 0 || request.TargetSlot == request.TicketSlot)
            return Reject("invalid-request", out rejection);

        var target = before.Find(request.Target);
        var ticket = before.Find(request.Ticket);
        if (target is null || ticket is null || target.Uid <= 0 || ticket.Uid <= 0)
            return Reject("item-missing", out rejection);
        if (target.Core.ItemId != request.TargetItemId)
            return Reject("target-mismatch", out rejection);
        if (target.Core.EquipmentLockId != 0 || ticket.Core.EquipmentLockId != 0 ||
            target.Core.IsRental || target.IsExpired(seconds) || ticket.IsExpired(seconds))
            return Reject("item-state", out rejection);

        LimitCubeDefinition definition;
        ItemTemplate ticketTemplate;
        ItemTemplate targetTemplate;
        ImmutableArray<ResolvedResult> results;
        try
        {
            definition = cubes.Get(ticket.Core.ItemId) ??
                throw new InvalidDataException("来源不是受支持的变更券");
            ticketTemplate = items.Get(ticket.Core.ItemId);
            targetTemplate = items.Get(target.Core.ItemId);
            results = definition.Results.Select(row =>
            {
                var template = items.Get(row.ItemId);
                ValidateResult(row, template);
                var core = InventoryRewardGrantService.Create(template, row.Count, now,
                    shopExpiration: ignoreAbsoluteExpiration, ignoreUsablePeriod: ignoreUsablePeriod);
                return new ResolvedResult(row, template, core);
            }).ToImmutableArray();
            if (targetTemplate.Kind == ItemKind.Equipment && cubes.IsBattleSpirit(targetTemplate.Id))
                throw new InvalidDataException("斗气装备不能通过变更券转换");
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
        {
            return Reject("resource", out rejection);
        }
        if (definition.ItemId != ticket.Core.ItemId ||
            ticketTemplate.Id != ticket.Core.ItemId || ticketTemplate.Kind != ticket.Core.Kind ||
            ticketTemplate.Kind != ItemKind.Consumable || !ticket.Core.IsStackable ||
            !ItemSlotBoundService.Allows(before, ticket.Position, ticketTemplate.Kind) ||
            targetTemplate.Id != target.Core.ItemId || targetTemplate.Kind != target.Core.Kind ||
            !ItemSlotBoundService.Allows(before, target.Position, targetTemplate.Kind))
            return Reject("template", out rejection);

        int? required = definition.RequiredTargetCount(target.Core.ItemId);
        if (required is null || required <= 0 || target.Core.Count < required ||
            !target.Core.IsStackable && required != 1 || results.IsDefaultOrEmpty)
            return Reject("target-condition", out rejection);

        var selection = (attributePairs ?? IndependentAttributePairCatalog.RequiredOnly)
            .Resolve(ticket.Core.ItemId, AttributePairEntryKind.LimitCube);
        if (selection.Kind == AttributePairResolutionKind.RequiredUnavailable)
            return Reject("selection-policy", out rejection);

        InventorySnapshot paid;
        var removeTarget = InventoryDeleteService.Plan(before, target.Position, target.Core.Value, required.Value);
        if (!removeTarget.Accepted)
            return Reject("target-delete", out rejection);
        var removeTicket = InventoryDeleteService.Plan(removeTarget.Plan!.After, ticket.Position, ticket.Core.Value, 1);
        if (!removeTicket.Accepted)
            return Reject("ticket-delete", out rejection);
        var materials = InventoryMaterialConsumptionService.Plan(removeTicket.Plan!.After,
            definition.Materials, pieces, now);
        if (!materials.Accepted)
            return Reject("materials", out rejection);
        paid = materials.Plan!.After;

        long totalWeight;
        long roll;
        ResolvedResult selected;
        byte[]? trace = null;
        try
        {
            if (selection.Policy is { } policy)
            {
                var sample = IndependentAttributePairSampler.Next(policy, Next);
                trace = [sample.Ordinal];
                roll = sample.Ordinal;
                totalWeight = 121;
                selected = results.Single(row => row.Rule.ItemId == sample.ItemId);
            }
            else
            {
                totalWeight = results.Aggregate(0L, (sum, row) => checked(sum + row.Rule.Weight));
                roll = Next(totalWeight);
                long remaining = roll;
                selected = results.First(row =>
                {
                    if (remaining < row.Rule.Weight) return true;
                    remaining -= row.Rule.Weight;
                    return false;
                });
            }
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or OverflowException)
        {
            return Reject("random", out rejection);
        }

        ItemCore outputCore = selected.Core;
        if (targetTemplate.Kind == ItemKind.Equipment && selected.Template.Kind == ItemKind.Equipment)
            outputCore = outputCore with
            {
                EnchantCardId = target.Core.EnchantCardId,
                EnchantUpgrade = target.Core.EnchantUpgrade,
                SealFlag = target.Core.SealFlag
            };
        var grant = InventoryRewardGrantService.TryGrantCore(character with { Inventory = paid },
            outputCore, selected.Template, pieces, now);
        if (!grant.Accepted)
            return Reject("capacity", out rejection);
        var after = grant.Plan!.After;
        var outputSlots = grant.Plan.ChangedSlots.Where(slot => slot.List == InventoryListType.Main)
            .Where(slot =>
            {
                var current = grant.Plan.AfterIndex.Find(slot);
                if (current?.Core.ItemId != selected.Rule.ItemId)
                    return false;
                var previous = grant.Plan.BeforeIndex.Find(slot);
                return previous?.Core.ItemId != current.Core.ItemId ||
                    current.Core.Count > previous.Core.Count;
            }).ToArray();
        if (outputSlots.Length != 1)
            return Reject("result-slot", out rejection);
        var output = grant.Plan.AfterIndex.Find(outputSlots[0]);
        if (output is null || output.IsExpired(seconds))
            return Reject("result-state", out rejection);

        long? deadline = EarliestDeadline(before, paid, output);
        receipt = new(ticket.Core.ItemId, target.Core.ItemId, selected.Rule.ItemId,
            checked((ushort)selected.Rule.Count), output.Position, roll, totalWeight)
        {
            SelectionTrace = trace,
            SelectionSummary = trace is null ? null : IndependentAttributePairSampler.Describe(selection.Policy!, trace)
        };
        return new(true, 0, new(before, after, "use_limit_cube",
            new InventoryCharacterPrecondition(character, deadline)));

        void ValidateResult(LimitCubeResultItem row, ItemTemplate template)
        {
            if (row.Count > ushort.MaxValue || template.Id != row.ItemId ||
                ItemSlotBoundService.List(template.Kind) != InventoryListType.Main ||
                CurrencyWallet.RewardKind(row.ItemId) is not null || pieces.ByPiece.ContainsKey(row.ItemId) ||
                contracts.Recognizes(row.ItemId) || directActivationItems?.Contains(row.ItemId) == true ||
                template.AcquisitionContract is not null || template.IsPremiumService ||
                template.Kind == ItemKind.Equipment && cubes.IsBattleSpirit(template.Id))
                throw new InvalidDataException("变更券结果不是可直接入Main的实体物品");
        }
    }

    long Next(long bound)
    {
        if (bound <= 0)
            throw new InvalidDataException("变更券随机上界无效");
        long value = draw(bound);
        if (value < 0 || value >= bound)
            throw new InvalidDataException("变更券随机值越界");
        return value;
    }

    static long? EarliestDeadline(InventorySnapshot before, InventorySnapshot paid, InventoryItem output)
    {
        var paidIndex = new InventoryIndex(paid);
        long? deadline = null;
        foreach (var item in before.Items)
        {
            var remaining = item.Uid == 0 ? paidIndex.Find(item.Position) : paidIndex.FindUid(item.Uid);
            if (remaining is not null && remaining.Core.Count >= item.Core.Count)
                continue;
            Add(item.ExpireTime);
        }
        Add(output.ExpireTime);
        return deadline;

        void Add(int expires)
        {
            if (expires > 0 && (deadline is null || expires < deadline.Value))
                deadline = expires;
        }
    }

    static InventoryResult Reject(string reason, out string? rejection)
    {
        rejection = reason;
        return InventoryResult.Reject(Rejection);
    }

    sealed record ResolvedResult(LimitCubeResultItem Rule, ItemTemplate Template, ItemCore Core);
}
