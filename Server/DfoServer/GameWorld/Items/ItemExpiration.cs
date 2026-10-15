using System.Globalization;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record ItemExpiration(long AbsoluteTime = 0, int Days = 0)
{
    // 资源保留远期原值；新实例的期限仍映射到匹配客户端的有符号32位上限。
    public int Resolve(DateTimeOffset now, bool ignoreAbsoluteExpiration = false, bool ignoreUsablePeriod = false)
    {
        if (Days > 0 && !ignoreUsablePeriod)
            return (int)Math.Min(int.MaxValue, now.ToUnixTimeSeconds() + (long)Days * 86400);
        if (AbsoluteTime > 0 && !ignoreAbsoluteExpiration)
            return (int)Math.Min(int.MaxValue, AbsoluteTime);
        // 模板带期限时，客户端不能用0取消其倒计时；只覆盖实际被忽略的期限，无期限物品仍为永久。
        return Days > 0 || AbsoluteTime > 0 ? int.MaxValue : 0;
    }

    // 兼容既有调用，只忽略绝对日期，不覆盖相对天数或永久物品。
    public int ResolveShop(DateTimeOffset now) => Resolve(now, ignoreAbsoluteExpiration: true);

    public static ItemExpiration Parse(PvfDocument text)
    {
        int days = PvfScript.Integers(text.Section("usable period")).FirstOrDefault();
        if (days < 0)
            throw new InvalidDataException("PVF物品期限不能为负数");
        string value = text.Section("expiration date").Trim().Trim('`').Trim();
        if (value.Length == 0 || value == "0")
            return new(0, days);
        if (long.TryParse(value, out long unixTime) && unixTime >= 1_000_000_000)
            return new(unixTime, days);
        if (!DateTime.TryParseExact(value, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new InvalidDataException("PVF物品到期时间无效");
        long seconds = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), TimeSpan.FromHours(8)).ToUnixTimeSeconds();
        if (seconds <= 0)
            throw new InvalidDataException("PVF物品到期时间超出范围");
        return new(seconds, days);
    }
}
