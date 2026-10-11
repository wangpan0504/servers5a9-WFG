using System.Globalization;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record ItemExpiration(int AbsoluteTime = 0, int Days = 0)
{
    public int Resolve(DateTimeOffset now) => Days > 0 ? (int)Math.Min(int.MaxValue, now.ToUnixTimeSeconds() + (long)Days * 86400) : AbsoluteTime;

    // 商城购买专用：仅按 [usable period] 相对期限，不套用 [expiration date] 的绝对截止。
    // 商城现金商品常带活动结束时间（如 2020 新春礼包的 2020-04-09），该时间是销售截止而非物品失效时间；
    // 普通奖励（副本/任务/邮件）仍用 Resolve 的绝对过期语义。
    public int ResolveShop(DateTimeOffset now) => Days > 0 ? (int)Math.Min(int.MaxValue, now.ToUnixTimeSeconds() + (long)Days * 86400) : 0;

    public static ItemExpiration Parse(PvfDocument text)
    {
        int days = PvfScript.Integers(text.Section("usable period")).FirstOrDefault();
        if (days < 0)
            throw new InvalidDataException("PVF物品期限不能为负数");
        string value = text.Section("expiration date").Trim().Trim('`').Trim();
        if (value.Length == 0 || value == "0")
            return new(0, days);
        if (int.TryParse(value, out int unixTime) && unixTime >= 1_000_000_000)
            return new(unixTime, days);
        if (!DateTime.TryParseExact(value, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new InvalidDataException("PVF物品到期时间无效");
        long seconds = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), TimeSpan.FromHours(8)).ToUnixTimeSeconds();
        if (seconds is <= 0 or > int.MaxValue)
            throw new InvalidDataException("PVF物品到期时间超出范围");
        return new((int)seconds, days);
    }
}
