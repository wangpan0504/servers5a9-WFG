using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Trade;

internal sealed class PlayerTradeCommitUnknownException(Exception inner) : Exception("交易提交结果未知", inner) { }

internal sealed record TradeSettlementPlan(InventoryPlan First, InventoryPlan Second,
    ImmutableArray<(ushort TradeSlot, InventorySlot Destination)> FirstReceived,
    ImmutableArray<(ushort TradeSlot, InventorySlot Destination)> SecondReceived);

internal static class TradeSettlement
{
    public static TradeSettlementPlan? Plan(Character first, Character second, TradeTicket ticket)
    {
        if (first.Id != ticket.First.CharacterId || second.Id != ticket.Second.CharacterId ||
            !PlanSide(first.Inventory, ticket.FirstOffer, ticket.SecondOffer, out var firstPlan, out var firstReceived) ||
            !PlanSide(second.Inventory, ticket.SecondOffer, ticket.FirstOffer, out var secondPlan, out var secondReceived))
            return null;
        return new(firstPlan!, secondPlan!, firstReceived, secondReceived);
    }

    static bool PlanSide(InventorySnapshot before, TradeOffer outgoing, TradeOffer incoming,
        out InventoryPlan? plan, out ImmutableArray<(ushort TradeSlot, InventorySlot Destination)> received)
    {
        plan = null;
        received = [];
        long gold = (long)before.Gold - outgoing.Gold + incoming.Gold;
        if (outgoing.Gold > before.Gold || gold < 0 || gold > int.MaxValue ||
            outgoing.Items.Select(line => line.Item.Uid).Distinct().Count() != outgoing.Items.Length)
            return false;
        var items = before.Items.ToBuilder();
        foreach (var line in outgoing.Items)
        {
            if (line.Item.Uid <= 0 || line.Item.Position.List != ItemSlotBoundService.List(line.Item.Core.Kind)) return false;
            int index = -1;
            for (int candidate = 0; candidate < items.Count; candidate++)
                if (items[candidate].Uid == line.Item.Uid) { index = candidate; break; }
            if (index < 0 || items[index] != line.Item || line.Count <= 0 || line.Count > line.Item.Core.Count)
                return false;
            if (line.Count == line.Item.Core.Count)
                items.RemoveAt(index);
            else
                items[index] = line.Item with { Core = line.Item.Core with { Value = line.Item.Core.Count - line.Count } };
        }
        var destinations = ImmutableArray.CreateBuilder<(ushort, InventorySlot)>();
        var used = items.Select(item => item.Position).ToHashSet();
        foreach (var line in incoming.Items)
        {
            if (line.Item.Uid <= 0 || line.Count <= 0 || line.Count > line.Item.Core.Count) return false;
            var list = ItemSlotBoundService.List(line.Item.Core.Kind);
            var (start, end) = ItemSlotBoundService.Range(line.Item.Core.Kind, before);
            InventorySlot? destination = null;
            for (short index = start; index <= end; index++)
            {
                var candidate = new InventorySlot(list, index);
                if (!used.Contains(candidate) && ItemSlotBoundService.Allows(before, candidate, line.Item.Core.Kind))
                {
                    destination = candidate;
                    break;
                }
            }
            if (destination is not { } position) return false;
            bool split = line.Count != line.Item.Core.Count;
            items.Add(line.Item with
            {
                Uid = split ? 0 : line.Item.Uid,
                Position = position,
                Core = split ? line.Item.Core with { Value = line.Count } : line.Item.Core
            });
            used.Add(position);
            destinations.Add((line.Slot, position));
        }
        plan = new(before, before with { Items = items.ToImmutable(), Gold = (uint)gold }, "player_trade");
        received = destinations.ToImmutable();
        return true;
    }
}
