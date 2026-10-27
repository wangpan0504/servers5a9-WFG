using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Models;

internal sealed record ServerItemUsage(byte Type, int ItemId, int Day, int Used);
internal sealed record ServerItemMailDay(int ItemId, int Day);
internal sealed record ServerItemRefillDay(int ItemId, int Day);
internal sealed record AccountItemRefillState(int Day, ushort CharacterId, bool Completed);
internal sealed record ServerItemCycleCommit(Character Character, bool MailboxChanged);
internal sealed record ServerItemUsageState(int RefillDay = 0)
{
    public long LastSweepAt { get; init; }
    public bool PendingAccountRefills { get; init; }
    public ImmutableArray<ServerItemUsage> Uses { get; init; } = [];
    public ImmutableArray<ServerItemMailDay> Mails { get; init; } = [];
    public ImmutableArray<ServerItemRefillDay> Refills { get; init; } = [];
    public static ServerItemUsageState Empty { get; } = new();
    public void Validate()
    {
        if (LastSweepAt < 0 || RefillDay < 0 || RefillDay > DateOnly.MaxValue.DayNumber || Uses.IsDefault || Mails.IsDefault || Refills.IsDefault ||
            Uses.Length > 4096 || Mails.Length > 4096 || Refills.Length > 4096 || Uses.Any(row => row.Type > 3 || row.ItemId < 0 || row.Day < 1 ||
                row.Day > DateOnly.MaxValue.DayNumber || row.Used < 0) ||
            Uses.Select(row => (row.Type, row.ItemId)).Distinct().Count() != Uses.Length ||
            Mails.Any(row => row.ItemId <= 0 || row.Day < 1 || row.Day > DateOnly.MaxValue.DayNumber) ||
            Mails.Select(row => row.ItemId).Distinct().Count() != Mails.Length ||
            Refills.Any(row => row.ItemId <= 0 || row.Day < 1 || row.Day > DateOnly.MaxValue.DayNumber) ||
            Refills.Select(row => row.ItemId).Distinct().Count() != Refills.Length)
            throw new InvalidDataException("服务端物品周期状态无效");
    }
    public bool Same(ServerItemUsageState other) => RefillDay == other.RefillDay && LastSweepAt == other.LastSweepAt && PendingAccountRefills == other.PendingAccountRefills &&
        Uses.SequenceEqual(other.Uses) && Mails.SequenceEqual(other.Mails) && Refills.SequenceEqual(other.Refills);

    public static bool TryConsume(ServerItemUsageState state, ServerItemUsageCatalog catalog, byte action, int itemId, int count,
        DateTimeOffset now, TimeZoneInfo zone, out ServerItemUsageState after)
    {
        state.Validate(); after = state;
        var rules = catalog.Limits.Where(row => row.ItemId == itemId && (action == 0 ? row.Type <= 1 : row.Type == action)).ToArray();
        if (rules.Length == 0) return true;
        if (count <= 0) return false;
        int day = FatigueDailyReset.Day(now, zone);
        var uses = state.Uses;
        foreach (var rule in rules)
        {
            var prior = uses.SingleOrDefault(row => row.Type == rule.Type && row.ItemId == itemId);
            if (prior?.Day > day) return false;
            int used = prior?.Day == day ? prior.Used : 0;
            if ((long)used + count > rule.Maximum) return false;
            uses = uses.Where(row => row.Type != rule.Type || row.ItemId != itemId)
                .Append(new(rule.Type, itemId, day, used + count)).OrderBy(row => row.Type).ThenBy(row => row.ItemId).ToImmutableArray();
        }
        after = state with { Uses = uses }; return true;
    }
}
