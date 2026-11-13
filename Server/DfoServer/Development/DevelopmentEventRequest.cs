using System.Globalization;
using DfoServer.Game.Events;

namespace DfoServer.Development;

internal sealed record DevelopmentEventRequest(string Operation, ushort EventId, DateTimeOffset Start = default,
    DateTimeOffset End = default)
{
    public const string Usage = "event status|on|off|auto <活动ID> | event schedule <活动ID> <开始ISO8601> <结束ISO8601> | event initialize-default <312|2520>；活动及时间表能力见当前活动目录，包含228累积在线得好礼；时间须带Z或+08:00等时区；312/2520初始化仅在尚无配置时开启，2520需要启动前装配有效冻结季，已有关闭及时间表保持；所有已登记且资源已装配的活动默认开启，228不支持时间表，2532未配置或auto默认显示，266无强制状态且未配置时间表时默认常驻，显式off和已有时间表仍生效。10005星河裂缝未配置时默认开启；人工关闭及已设时间表保留。";

    public static DevelopmentEventRequest Parse(string[] tokens)
    {
        if (tokens.Length < 2 || !ushort.TryParse(tokens[1], NumberStyles.None, CultureInfo.InvariantCulture, out ushort id))
            throw new InvalidDataException(Usage);
        ServerEventService.RequireRegistered(id);
        if (tokens.Length == 2 && tokens[0] is "status" or "on" or "off" or "auto")
            return new(tokens[0], id);
        if (tokens.Length == 2 && tokens[0] == "initialize-default" && id is ServerEventService.Strongway2019Id or ServerEventService.BattlePassId)
            return new(tokens[0], id);
        if (tokens.Length != 4 || tokens[0] != "schedule" || !ServerEventService.Definition(id).SupportsSchedule)
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

    public ServerEventState Execute(ServerEventService events) => Execute(events, out _);

    public ServerEventState Execute(ServerEventService events, out bool? initialized)
    {
        initialized = null;
        if (Operation == "initialize-default")
        {
            var result = events.InitializeDefault(EventId);
            initialized = result.Initialized;
            return result.State;
        }
        return Operation switch
        {
            "status" => events.Read(EventId),
            "on" => events.Override(EventId, true),
            "off" => events.Override(EventId, false),
            "auto" => events.Override(EventId, null),
            "schedule" => events.Schedule(EventId, Start, End),
            _ => throw new InvalidDataException(Usage)
        };
    }
}
