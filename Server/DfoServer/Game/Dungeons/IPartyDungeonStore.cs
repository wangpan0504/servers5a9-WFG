using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Dungeons;
using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Dungeons;

internal sealed record PartyDungeonReservation(AccountOwners.Lease Owner, Character Expected,
    uint NextObjectKey, DungeonEntryCharge Charge, PartyDungeonIncount? Incount = null);
internal sealed record PartyDungeonIncount(int Dungeon, DungeonIncountPeriod Period, DungeonIncountRow? Row,
    ImmutableArray<AccountIncountCharge> Account, bool Increment);
internal sealed record PartyDungeonEntryCommit(Character Character, ImmutableArray<DungeonEnterCount> EnterCounts);
internal sealed record PartyDungeonExitReservation(AccountOwners.Lease Owner, Character Expected, Location Position, InventoryPlan? Inventory)
{
    public bool Changed => Position != Expected.Position || Inventory is not null;
}
internal enum PartyDungeonExitKind { SelectionReturn, ResultReturn, ResultSelection, HellSelection }

// 全员入场费用和尝试号必须在同一事务提交，不能由协调器依次调用单人提交。
internal interface IPartyDungeonStore
{
    PartyDungeonEntryCommit[] CommitPartyDungeonEntry(IReadOnlyList<PartyDungeonReservation> reservations);
    Character[] CommitPartyDungeonExit(IReadOnlyList<PartyDungeonExitReservation> reservations);
}
