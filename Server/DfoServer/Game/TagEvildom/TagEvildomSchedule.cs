namespace DfoServer.Game.TagEvildom;

// 与本版etc的5/30/10及每日06点合同一致；所有业务日均按UTC+8计算。
internal static class TagEvildomSchedule
{
    public static bool CanEnter(DateTimeOffset now)
    {
        var local = now.ToOffset(TimeSpan.FromHours(8)).TimeOfDay;
        return local < new TimeSpan(5, 30, 0) || local >= new TimeSpan(6, 0, 0);
    }

    public static long ReturnAt(long admittedAt)
    {
        var local = DateTimeOffset.FromUnixTimeSeconds(admittedAt).ToOffset(TimeSpan.FromHours(8));
        var day = local.Date.AddHours(5).AddMinutes(40);
        if (day <= local.DateTime) day = day.AddDays(1);
        return new DateTimeOffset(day, TimeSpan.FromHours(8)).ToUnixTimeSeconds();
    }
}
