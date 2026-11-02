namespace DfoServer.Game.Events;

internal sealed record ServerEventSchedule(ushort EventId, long Revision = 0, long? StartsAt = null,
    long? EndsAt = null, bool? OverrideActive = null)
{
    public bool IsActive(DateTimeOffset now) => OverrideActive ??
        (StartsAt is { } start && EndsAt is { } end && now.ToUnixTimeSeconds() >= start && now.ToUnixTimeSeconds() < end ||
         EventId == ServerEventService.MileageShopId && StartsAt is null && EndsAt is null);

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
    public const ushort NolanVisibilityId = 2532;
    public const ushort MileageShopId = 266;
    public const ushort Strongway2019Id = 312;
    public event Action<ushort>? Changed;
    readonly TimeProvider time = clock ?? TimeProvider.System;
    long strongway2019DisplayRevision;
    internal long Strongway2019DisplayRevision => Volatile.Read(ref strongway2019DisplayRevision);

    public ServerEventState Read(ushort eventId)
    {
        RequireRegistered(eventId);
        var schedule = store.Read(eventId);
        schedule.Validate();
        return State(schedule);
    }

    public ServerEventState Override(ushort eventId, bool? active) => Update(eventId,
        value => value with { OverrideActive = active });

    public ServerEventState Schedule(ushort eventId, DateTimeOffset start, DateTimeOffset end)
    {
        if (eventId == NolanVisibilityId)
            throw new InvalidDataException("2532仅支持常驻开关，不支持时间表");
        return Update(eventId,
            value => value with { StartsAt = start.ToUnixTimeSeconds(), EndsAt = end.ToUnixTimeSeconds(), OverrideActive = null });
    }

    ServerEventState Update(ushort eventId, Func<ServerEventSchedule, ServerEventSchedule> edit)
    {
        var before = Read(eventId).Schedule;
        var desired = edit(before);
        desired.Validate();
        var committed = store.Save(before, desired);
        if (committed != before)
        {
            if (eventId == Strongway2019Id) Interlocked.Increment(ref strongway2019DisplayRevision);
            Changed?.Invoke(eventId);
        }
        return State(committed);
    }

    ServerEventState State(ServerEventSchedule schedule) => new(schedule,
        schedule.EventId == NolanVisibilityId ? schedule.OverrideActive ?? true : schedule.IsActive(time.GetUtcNow()));

    public static void RequireRegistered(ushort eventId)
    {
        if (eventId != DoubleGoldCardId && eventId != NolanVisibilityId && eventId != MileageShopId && eventId != Strongway2019Id)
            throw new InvalidDataException("尚未登记该服务端活动；黄金卡牌双倍奖励为64，积分商城为266，2019强者之路为312，诺兰常驻状态为2532，公告74不是活动开关");
    }
}
