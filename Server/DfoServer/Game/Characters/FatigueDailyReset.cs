namespace DfoServer.Game.Characters;

internal static class FatigueDailyReset
{
    public static int Day(DateTimeOffset now, TimeZoneInfo zone) =>
        DfoServer.GameWorld.Time.ServiceDay.Number(now, zone);

    public static DateTimeOffset NextReset(DateTimeOffset now, TimeZoneInfo zone) =>
        DfoServer.GameWorld.Time.ServiceDay.NextReset(now, zone);

    public static CharacterFatigueState Restore(CharacterFatigueState state, int day, bool blackDiamond)
    {
        state.Validate();
        if (day <= 0 || day > DateOnly.MaxValue.DayNumber) throw new ArgumentOutOfRangeException(nameof(day));
        if (day <= state.ResetDay) return state;
        var capacity = state.ForBlackDiamond(blackDiamond);
        return capacity with { Used = 0, Remaining = capacity.Maximum, ResetDay = day };
    }
}
