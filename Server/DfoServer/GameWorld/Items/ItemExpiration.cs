using System.Globalization;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record ItemExpiration(long AbsoluteTime = 0, int Days = 0, int DailyDelete = 0)
{
    // 完整未知整型声明仅保留原值；未证语义不参与新实例发放。
    public bool SupportsCreation => DailyDelete is 0 or 1;

    // 资源保留远期原值；新实例的期限仍映射到匹配客户端的有符号32位上限。
    public int Resolve(DateTimeOffset now, bool ignoreAbsoluteExpiration = false, bool ignoreUsablePeriod = false)
    {
        if (!SupportsCreation) throw new InvalidDataException("PVF每日删除声明尚不支持新建实例");
        int ordinary = Days > 0 && !ignoreUsablePeriod
            ? (int)Math.Min(int.MaxValue, now.ToUnixTimeSeconds() + (long)Days * 86400)
            : AbsoluteTime > 0 && !ignoreAbsoluteExpiration
                ? (int)Math.Min(int.MaxValue, AbsoluteTime)
                : Days > 0 || AbsoluteTime > 0 ? int.MaxValue : 0;
        if (DailyDelete == 0) return ordinary;
        // 本服新实例每日到期政策：UTC+8六点；商城忽略普通期限不能取消每日期限。
        if (now.ToUnixTimeSeconds() >= int.MaxValue)
            throw new InvalidDataException("每日物品截止超出协议范围");
        var local = now.ToOffset(TimeSpan.FromHours(8));
        var next = new DateTimeOffset(local.Year, local.Month, local.Day, 6, 0, 0, local.Offset);
        if (next <= now) next = next.AddDays(1);
        long daily = next.ToUnixTimeSeconds();
        if (daily <= 0 || daily > int.MaxValue)
            throw new InvalidDataException("每日物品截止超出协议范围");
        return ordinary > 0 ? Math.Min(ordinary, (int)daily) : (int)daily;
    }

    // 兼容既有调用，只忽略绝对日期，不覆盖相对天数或永久物品。
    public int ResolveShop(DateTimeOffset now) => Resolve(now, ignoreAbsoluteExpiration: true);

    public static ItemExpiration Parse(PvfDocument text)
    {
        int? daily = text.ReadRootInt32("daily delete item");
        int days = PvfScript.Integers(text.Section("usable period")).FirstOrDefault();
        if (days < 0)
            throw new InvalidDataException("PVF物品期限不能为负数");
        string value = text.Section("expiration date").Trim().Trim('`').Trim();
        if (value.Length == 0 || value == "0")
            return new(0, days, daily ?? 0);
        if (long.TryParse(value, out long unixTime) && unixTime >= 1_000_000_000)
            return new(unixTime, days, daily ?? 0);
        if (!DateTime.TryParseExact(value, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new InvalidDataException("PVF物品到期时间无效");
        long seconds = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), TimeSpan.FromHours(8)).ToUnixTimeSeconds();
        if (seconds <= 0)
            throw new InvalidDataException("PVF物品到期时间超出范围");
        return new(seconds, days, daily ?? 0);
    }
}
