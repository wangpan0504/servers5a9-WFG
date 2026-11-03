using System.Globalization;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record ItemExpiration(long AbsoluteTime = 0, int Days = 0, int DailyDelete = 0, int? WeeklyDeleteDay = null)
{
    // UTC+8的2037-12-31 06:00:00，避开客户端2038年时间换算边界。
    public const int MaximumUnixTime = 2_145_823_200;
    public static int ForClient(int expiresAt) => Math.Min(expiresAt, MaximumUnixTime);

    // 完整未知整型声明仅保留原值；未证语义不参与新实例发放。
    public bool SupportsCreation => DailyDelete is 0 or 1 && (WeeklyDeleteDay is null or >= 0 and <= 6);

    // 资源保留远期原值；新实例的绝对和相对期限统一截到客户端安全上限。
    public int Resolve(DateTimeOffset now, bool ignoreAbsoluteExpiration = false, bool ignoreUsablePeriod = false)
    {
        if (!SupportsCreation) throw new InvalidDataException("PVF周期删除声明尚不支持新建实例");
        int ordinary = Days > 0 && !ignoreUsablePeriod
            ? (int)Math.Min(MaximumUnixTime, now.ToUnixTimeSeconds() + (long)Days * 86400)
            : AbsoluteTime > 0 && !ignoreAbsoluteExpiration
                ? (int)Math.Min(MaximumUnixTime, AbsoluteTime)
                : Days > 0 || AbsoluteTime > 0 ? MaximumUnixTime : 0;
        if (DailyDelete == 0 && WeeklyDeleteDay is null) return ordinary;
        // 本版每周标签0=周日、1..6=周一..周六；沿本服UTC+8六点，创建时冻结，不在重登时续期。
        // 商城忽略普通期限不能取消每日/每周删除；所有有效期限只取更早者。
        long periodic = MaximumUnixTime;
        if (now.ToUnixTimeSeconds() < MaximumUnixTime)
        {
            var local = now.ToOffset(TimeSpan.FromHours(8));
            var today = new DateTimeOffset(local.Year, local.Month, local.Day, 6, 0, 0, local.Offset);
            if (DailyDelete == 1)
            {
                var next = today <= now ? today.AddDays(1) : today;
                periodic = Math.Min(periodic, next.ToUnixTimeSeconds());
            }
            if (WeeklyDeleteDay is { } day)
            {
                var next = today.AddDays((day - (int)local.DayOfWeek + 7) % 7);
                if (next <= now) next = next.AddDays(7);
                periodic = Math.Min(periodic, next.ToUnixTimeSeconds());
            }
        }
        if (periodic <= 0)
            throw new InvalidDataException("周期物品截止超出协议范围");
        return ordinary > 0 ? Math.Min(ordinary, (int)periodic) : (int)periodic;
    }

    // period:off / ClearedExpire：usable period 与 expiration date 仍可清掉；
    // 带 [daily delete item] 1 或有效 [weekly delete item] 时禁止清成 0，改走 Resolve。
    public int ForClearedPeriod(DateTimeOffset now) =>
        DailyDelete == 1 || WeeklyDeleteDay is >= 0 and <= 6
            ? Resolve(now, ignoreAbsoluteExpiration: true, ignoreUsablePeriod: true)
            : Days > 0 || AbsoluteTime > 0 ? int.MaxValue : 0;

    // 兼容既有调用，只忽略绝对日期，不覆盖相对天数或永久物品。
    public int ResolveShop(DateTimeOffset now) => Resolve(now, ignoreAbsoluteExpiration: true);

    public static ItemExpiration Parse(PvfDocument text)
    {
        int? daily = text.ReadRootInt32("daily delete item");
        int? weekly = text.ReadRootInt32("weekly delete item", allowIdenticalDuplicates: true);
        int days = PvfScript.Integers(text.Section("usable period")).FirstOrDefault();
        if (days < 0)
            throw new InvalidDataException("PVF物品期限不能为负数");
        string value = text.Section("expiration date").Trim().Trim('`').Trim();
        return new(ParseAbsoluteTime(value), days, daily ?? 0, weekly);
    }

    internal static long ParseAbsoluteTime(string value)
    {
        if (value.Length == 0 || value == "0")
            return 0;
        if (long.TryParse(value, out long unixTime) && unixTime >= 1_000_000_000)
            return unixTime;
        if (!DateTime.TryParseExact(value, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new InvalidDataException("PVF物品到期时间无效");
        long seconds = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), TimeSpan.FromHours(8)).ToUnixTimeSeconds();
        if (seconds <= 0)
            throw new InvalidDataException("PVF物品到期时间超出范围");
        return seconds;
    }
}
