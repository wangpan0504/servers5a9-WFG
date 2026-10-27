namespace DfoServer.GameWorld.Time;

// 疲劳及服务端物品规则使用服务器本地06:00，不改变其他业务的日界。
internal static class ServiceDay
{
    public static DateOnly Date(DateTimeOffset now, TimeZoneInfo? zone = null) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone ?? TimeZoneInfo.Local).DateTime.AddHours(-6));

    public static int Number(DateTimeOffset now, TimeZoneInfo? zone = null) => Date(now, zone).DayNumber;
    public static DateTimeOffset At(DateTime local, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // 夏令时跨过日切时取其后第一个有效时刻，重复时刻取第一次，账本日戳保证不再重置。
        for (int minute = 0; zone.IsInvalidTime(local) && minute < 1440; minute++) local = local.AddMinutes(1);
        return zone.IsAmbiguousTime(local) ? new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).ToUniversalTime() :
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }

    public static DateTimeOffset NextReset(DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var local=Date(now,zone).AddDays(1).ToDateTime(new TimeOnly(6,0));
        var next=At(local,zone);
        if (next>now) return next;
        // 大幅回拨可能让本地时间再次落在六点前；已过去的第一次六点不能形成负延迟。
        if (zone.IsAmbiguousTime(local))
            foreach(var offset in zone.GetAmbiguousTimeOffsets(local).OrderByDescending(value=>value))
                if (new DateTimeOffset(local,offset).ToUniversalTime() is var candidate && candidate>now) return candidate;
        return At(local.AddDays(1),zone);
    }
}
