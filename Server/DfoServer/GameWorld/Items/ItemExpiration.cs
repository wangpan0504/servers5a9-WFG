using System.Globalization;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record ItemExpiration(long AbsoluteTime = 0, int Days = 0)
{
    // 资源保留远期原值；新实例的期限仍映射到匹配客户端的有符号32位上限。
    public int Resolve(DateTimeOffset now) => (int)Math.Min(int.MaxValue,
        Days > 0 ? now.ToUnixTimeSeconds() + (long)Days * 86400 : AbsoluteTime);

    // 商城/礼包调试统一覆盖为协议上限：2038-01-19 11:14:07（UTC+8）。
    // 带PVF期限的客户端模板不会把0视为永久；正常购买和其他奖励仍使用 Resolve。
    public int ResolveShop(DateTimeOffset _) => int.MaxValue;

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
