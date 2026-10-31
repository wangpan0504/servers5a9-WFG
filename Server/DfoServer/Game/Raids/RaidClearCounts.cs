using System.Collections.Immutable;
using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Raids;

internal readonly record struct RaidClearPeriod(byte Type, int Week, long DayStart);
internal readonly record struct RaidClearCount(byte Type, uint DailyUsed, uint WeeklyUsed);

internal static class RaidClearCounts
{
    internal static int Week(byte type, DateTimeOffset now, RaidWeeklyCycle? lukeCycle = null) => type switch
    {
        1 when lukeCycle is not null => lukeCycle.Key(now),
        2 => FiendwarRewards.Cycle.Key(now),
        _ => DungeonIncountPolicy.Period(now).Week
    };

    internal static ImmutableArray<RaidClearPeriod> Periods(DateTimeOffset now, RaidWeeklyCycle? lukeCycle = null)
    {
        long start = new DateTimeOffset(now.UtcDateTime.AddHours(2).Date, TimeSpan.Zero).AddHours(-2).ToUnixTimeSeconds();
        return [.. Enumerable.Range(0, 4).Select(type => new RaidClearPeriod((byte)type, Week((byte)type, now, lukeCycle), start))];
    }
}
