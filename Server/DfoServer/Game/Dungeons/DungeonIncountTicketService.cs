using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonIncountTicketPlan(Character Expected, InventoryItem Source, InventoryPlan Inventory,
    DungeonIncountRow Row, DungeonIncountPeriod Period, int WeeklyResetDay);

internal interface IDungeonIncountTicketStore
{
    DungeonIncountCommit CommitDungeonIncountTicket(AccountOwners.Lease owner, DungeonIncountTicketPlan plan, DateTimeOffset now);
}

internal sealed record DungeonIncountTicketResult(ConsumableUseOutcome Outcome, string Reason, ConsumableUseGuard Guard,
    InventoryResult? Inventory = null, ImmutableArray<DungeonEnterCount> Counts = default);

internal sealed class DungeonIncountTicketService(ItemCatalog items, DungeonIncountCatalog incounts, TownCatalog towns, TimeProvider? clock = null)
{
    public const uint Action = 214;
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public DungeonIncountTicketPlan Prepare(Character expected, InventorySlot slot)
    {
        var source = expected.Inventory.Find(slot) ?? throw new InvalidDataException("追加券源不存在");
        var definition = items.ActionConsumable(source.Core.ItemId);
        if (definition is not ({ Label: "[add dungeon incount item limit count]", Parameters: [> 0, 0, 0] } or
            { Label: "[add dungeon incount item limit count]", Parameters: [8523, -1, -1] }) ||
            incounts.Find(definition.Parameters[0]) is not { Limit: > 0, WeeklyLimit: > 0 } row)
            throw new InvalidDataException("追加券动作、参数或次数表不符");
        var now = Clock.GetUtcNow();
        string place = towns.IsRoom(expected.Position, QuestEligibility.Completed(expected)) ? "[seria room]" : "[village]";
        var result = InventoryActionConsumableService.Plan(expected, slot, definition, place, Clock, now);
        if (!result.Accepted) throw new InvalidDataException("追加券源或使用资格不符");
        int reset = incounts.WeeklyResetDay(row.CountKey);
        return new(expected, source, result.Plan!, row, DungeonIncountPolicy.Period(now, reset), reset);
    }
}
