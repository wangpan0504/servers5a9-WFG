using System.Globalization;
using DfoServer.Game.Events;

namespace DfoServer.Development;

internal sealed record DevelopmentEventRequest(string Operation, ushort EventId, DateTimeOffset Start = default,
    DateTimeOffset End = default)
{
    public const string Usage = "event status|on|off|auto <活动ID> | event schedule <活动ID> <开始ISO8601> <结束ISO8601>；目前登记64黄金卡牌双倍奖励和2532诺兰常驻状态；schedule仅支持64，时间须带Z或+08:00等时区；2532未配置或auto默认显示诺兰，off持久隐藏，不使用历史日期规则。";

    public static DevelopmentEventRequest Parse(string[] tokens)
    {
        if (tokens.Length < 2 || !ushort.TryParse(tokens[1], NumberStyles.None, CultureInfo.InvariantCulture, out ushort id))
            throw new InvalidDataException(Usage);
        ServerEventService.RequireRegistered(id);
        if (tokens.Length == 2 && tokens[0] is "status" or "on" or "off" or "auto")
            return new(tokens[0], id);
        if (tokens.Length != 4 || tokens[0] != "schedule")
            throw new InvalidDataException(Usage);
        DateTimeOffset Time(string value)
        {
            string[] formats = ["yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss'Z'"];
            if (!DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
                throw new InvalidDataException(Usage);
            return time;
        }
        var start = Time(tokens[2]);
        var end = Time(tokens[3]);
        new ServerEventSchedule(id, StartsAt: start.ToUnixTimeSeconds(), EndsAt: end.ToUnixTimeSeconds()).Validate();
        return new("schedule", id, start, end);
    }

    public ServerEventState Execute(ServerEventService events) => Operation switch
    {
        "status" => events.Read(EventId),
        "on" => events.Override(EventId, true),
        "off" => events.Override(EventId, false),
        "auto" => events.Override(EventId, null),
        "schedule" => events.Schedule(EventId, Start, End),
        _ => throw new InvalidDataException(Usage)
    };
}
