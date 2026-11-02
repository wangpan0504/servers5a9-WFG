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
    DungeonIncountRow Row, DungeonIncountPeriod Period, int WeeklyResetDay, int Amount = 1, bool Reset = false);

internal interface IDungeonIncountTicketStore
{
    DungeonIncountCommit CommitDungeonIncountTicket(AccountOwners.Lease owner, DungeonIncountTicketPlan plan, DateTimeOffset now);
}

internal sealed record DungeonIncountTicketResult(ConsumableUseOutcome Outcome, string Reason, ConsumableUseGuard Guard,
    InventoryResult? Inventory = null, ImmutableArray<DungeonEnterCount> Counts = default);

internal sealed class DungeonIncountTicketService(ItemCatalog items, DungeonIncountCatalog incounts, TownCatalog towns, TimeProvider? clock = null)
{
    public const uint Action = 214;
    public const uint AddDaybreakAction = 213;
    public const uint ResetDaybreakAction = 221;
    public static bool Handles(uint action) => action is Action or AddDaybreakAction or ResetDaybreakAction;
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public DungeonIncountTicketPlan Prepare(Character expected, InventorySlot slot)
    {
        var source = expected.Inventory.Find(slot) ?? throw new InvalidDataException("追加券源不存在");
        var definition = items.ActionConsumable(source.Core.ItemId) ?? throw new InvalidDataException("追加券动作目录不存在");
        int amount = 1;
        bool reset = false;
        DungeonIncountRow? row;
        if (definition.Label == "[add crackofdaybreak dungeon enter count]" && definition.Parameters.Length == 1 &&
            definition.Parameters[0] is > 0 and <= 10000)
        {
            amount = definition.Parameters[0];
            row = incounts.Find(530);
        }
        else if (definition.Label == "[reset crackofdaybreak dungeon enter count]" && definition.Parameters.Length == 0)
        {
            reset = true;
            row = incounts.Find(530);
        }
        else if (definition.Label == "[add dungeon incount item limit count]" &&
            (definition.Parameters is [> 0, 0, 0] or [8523, -1, -1]))
        {
            row = incounts.Find(definition.Parameters[0]);
        }
        else throw new InvalidDataException("追加券动作、参数或次数表不符");
        if (row is not { Limit: > 0 })
            throw new InvalidDataException("追加券动作、参数或次数表不符");
        var now = Clock.GetUtcNow();
        string place = towns.IsRoom(expected.Position, QuestEligibility.Completed(expected)) ? "[seria room]" : "[village]";
        var result = InventoryActionConsumableService.Plan(expected, slot, definition, place, Clock, now);
        if (!result.Accepted) throw new InvalidDataException("追加券源或使用资格不符");
        int resetDay = incounts.WeeklyResetDay(row.CountKey);
        return new(expected, source, result.Plan!, row, DungeonIncountPolicy.Period(now, resetDay), resetDay, amount, reset);
    }
}
