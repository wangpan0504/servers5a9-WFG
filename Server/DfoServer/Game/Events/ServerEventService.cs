namespace DfoServer.Game.Events;

internal sealed record ServerEventSchedule(ushort EventId, long Revision = 0, long? StartsAt = null,
    long? EndsAt = null, bool? OverrideActive = null)
{
    public bool IsActive(DateTimeOffset now) => OverrideActive ??
        (StartsAt is { } start && EndsAt is { } end && now.ToUnixTimeSeconds() >= start && now.ToUnixTimeSeconds() < end);

    public void Validate()
    {
        if (EventId == 0 || Revision < 0 || StartsAt.HasValue != EndsAt.HasValue ||
            StartsAt is { } start && (start < 0 || EndsAt <= start || EndsAt > 253402300799))
            throw new InvalidDataException("活动时间表或版本无效");
    }
}

internal sealed record ServerEventState(ServerEventSchedule Schedule, bool Active);

internal interface IServerEventStore
{
    ServerEventSchedule Read(ushort eventId);
    ServerEventSchedule Save(ServerEventSchedule expected, ServerEventSchedule desired);
}

internal sealed class ServerEventService(IServerEventStore store, TimeProvider? clock = null)
{
    public const ushort DoubleGoldCardId = 64;
    readonly TimeProvider time = clock ?? TimeProvider.System;

    public ServerEventState Read(ushort eventId)
    {
        RequireRegistered(eventId);
        var schedule = store.Read(eventId);
        schedule.Validate();
        return new(schedule, schedule.IsActive(time.GetUtcNow()));
    }

    public ServerEventState Override(ushort eventId, bool? active) => Update(eventId,
        value => value with { OverrideActive = active });

    public ServerEventState Schedule(ushort eventId, DateTimeOffset start, DateTimeOffset end) => Update(eventId,
        value => value with { StartsAt = start.ToUnixTimeSeconds(), EndsAt = end.ToUnixTimeSeconds(), OverrideActive = null });

    ServerEventState Update(ushort eventId, Func<ServerEventSchedule, ServerEventSchedule> edit)
    {
        var before = Read(eventId).Schedule;
        var desired = edit(before);
        desired.Validate();
        var committed = store.Save(before, desired);
        return new(committed, committed.IsActive(time.GetUtcNow()));
    }

    public static void RequireRegistered(ushort eventId)
    {
        if (eventId != DoubleGoldCardId)
            throw new InvalidDataException("尚未登记该服务端活动；黄金卡牌双倍奖励为64，公告74不是活动开关");
    }
}
