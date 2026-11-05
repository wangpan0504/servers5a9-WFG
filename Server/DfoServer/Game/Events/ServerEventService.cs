namespace DfoServer.Game.Events;

internal sealed record ServerEventSchedule(ushort EventId, long Revision = 0, long? StartsAt = null,
    long? EndsAt = null, bool? OverrideActive = null)
{
    public bool IsActive(DateTimeOffset now) => OverrideActive ??
        (EventId is ServerEventService.NolanVisibilityId or ServerEventService.JonathanVisibilityId ||
         StartsAt is { } start && EndsAt is { } end && now.ToUnixTimeSeconds() >= start && now.ToUnixTimeSeconds() < end ||
         ServerEventService.DefaultActive(EventId) && StartsAt is null && EndsAt is null);

    public void Validate()
    {
        if (EventId == 0 || Revision < 0 || StartsAt.HasValue != EndsAt.HasValue ||
            StartsAt is { } start && (start < 0 || EndsAt <= start || EndsAt > 253402300799))
            throw new InvalidDataException("活动时间表或版本无效");
    }
}

internal sealed record ServerEventState(ServerEventSchedule Schedule, bool Active);
internal sealed record ServerEventChange(ServerEventState State, DateTimeOffset At, long Timestamp);
internal sealed record ServerEventInitialization(ServerEventState State, bool Initialized);

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
    public const ushort PcRoomTimePointId = 228;
    public const ushort DailyAttendanceId = 2370;
    ServerEventState dailyAttendanceState = new(new(DailyAttendanceId), false);
    public bool IsDailyAttendanceCurrent(long revision, bool enabled)
    {
        var current = Volatile.Read(ref dailyAttendanceState);
        return current.Schedule.Revision == revision && current.Active == enabled;
    }
    void ObserveDailyAttendance(ServerEventState state)
    {
        // 较早开始的只读查询可能晚于GM更新返回，不能让它倒退发送守卫的版本。
        while (true)
        {
            var previous = Volatile.Read(ref dailyAttendanceState);
            if (previous.Schedule.Revision > state.Schedule.Revision) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref dailyAttendanceState, state, previous), previous)) return;
        }
    }
    public const ushort WeeklyAttendanceId = 2208;
    public static IReadOnlyList<ServerEventDefinition> Definitions => ServerEventDefinition.Registered;
    readonly object updates = new();
    ServerEventSchedule endlessAdventureSchedule = store.Read(EndlessAdventurePolicy.EventId);
    public bool IsEndlessAdventureActive(DateTimeOffset now) => Volatile.Read(ref endlessAdventureSchedule).IsActive(now);
    public const ushort BattlePassId = 2520;
    public const ushort EpicRoadId = 10005;
    ServerEventSchedule battlePassSchedule = new(BattlePassId);
    DateTimeOffset? battlePassStart, battlePassEnd;
    bool battlePassDefaultEnabled, battlePassMissionsPrepared;
    public void ConfigureBattlePass(DateTimeOffset start, DateTimeOffset end, bool defaultEnabled, bool missionsPrepared = true)
    {
        battlePassStart = start; battlePassEnd = end; battlePassDefaultEnabled = defaultEnabled;
        battlePassMissionsPrepared = missionsPrepared;
        Volatile.Write(ref battlePassSchedule, store.Read(BattlePassId));
    }
    public bool IsBattlePassActive(DateTimeOffset now) => battlePassStart is { } start && battlePassEnd is { } end &&
        now >= start && now < end && BattlePassScheduleActive(Volatile.Read(ref battlePassSchedule), now, battlePassDefaultEnabled);
    public static bool BattlePassScheduleActive(ServerEventSchedule schedule, DateTimeOffset now, bool defaultEnabled) =>
        schedule.Revision == 0 ? defaultEnabled : schedule.IsActive(now);
    void RequireBattlePassPrepared(ushort id)
    {
        if (id == BattlePassId && (battlePassStart is null || !battlePassMissionsPrepared))
            throw new InvalidOperationException("探险记赛季未装配；请检查稳定赛季配置并重启服务端");
    }
    public const ushort SeriaLuckId = 2037;
    public const ushort GuildHongbaoId = 2378;
    public const ushort JonathanVisibilityId = 2395;
    public static bool DefaultActive(ushort id) => id is MileageShopId or NolanVisibilityId or
        SeriaLuckId or GuildHongbaoId or JonathanVisibilityId or EndlessAdventurePolicy.EventId or
        PcRoomTimePointId or DailyAttendanceId or WeeklyAttendanceId;
    long promotionRevision;
    public long PromotionRevision => Interlocked.Read(ref promotionRevision);
    public void NotifyPromotionChanged()
    {
        Interlocked.Increment(ref promotionRevision);
        Changed?.Invoke(new(new(new(10999), true), time.GetUtcNow(), time.GetTimestamp()));
    }
    readonly TimeProvider time = clock ?? TimeProvider.System;
    long strongway2019DisplayRevision;
    internal long Strongway2019DisplayRevision => Volatile.Read(ref strongway2019DisplayRevision);
    public event Action<ServerEventChange>? Changed;

    public static bool IsRegistered(ushort eventId) => ServerEventDefinition.Find(eventId) is not null;
    public static ServerEventDefinition Definition(ushort eventId) => ServerEventDefinition.Find(eventId)
        ?? throw new InvalidDataException("尚未登记该服务端活动；公告编号不能作为活动开关");
    internal T SynchronizeClock<T>(Func<T> action) { lock (updates) return action(); }

    public ServerEventState Read(ushort eventId)
    {
        RequireRegistered(eventId);
        var schedule = store.Read(eventId);
        schedule.Validate();
        if (eventId == BattlePassId) Volatile.Write(ref battlePassSchedule, schedule);
        if (eventId == EndlessAdventurePolicy.EventId) Volatile.Write(ref endlessAdventureSchedule, schedule);
        var state = State(schedule);
        if (eventId == DailyAttendanceId) ObserveDailyAttendance(state);
        return state;
    }

    public ServerEventState Override(ushort eventId, bool? active) => Update(eventId,
        value => value with { OverrideActive = active });

    /// <summary>仅白名单活动无配置时初始化；既有关闭、自动及时间表均由原版本保留。</summary>
    public ServerEventInitialization InitializeStrongwayDefault() => InitializeDefault(Strongway2019Id);
    public ServerEventInitialization InitializeDefault(ushort eventId)
    {
        if (eventId is not (Strongway2019Id or BattlePassId)) throw new InvalidDataException("该活动不支持首次默认初始化");
        RequireBattlePassPrepared(eventId);
        if (eventId == BattlePassId && (time.GetUtcNow() < battlePassStart || time.GetUtcNow() >= battlePassEnd))
            throw new InvalidOperationException("探险记已冻结赛季不在有效期内，不会自动换季");
        var initial = new ServerEventSchedule(eventId);
        var before = Read(eventId);
        if (before.Schedule != initial) return new(before, false);
        ServerEventSchedule committed;
        try { committed = store.Save(initial, initial with { OverrideActive = true }); }
        catch (InvalidOperationException)
        {
            // Save的唯一键/CAS拒绝竞争写入；重读后尊重人工选择，不重新执行普通on。
            var actual = Read(eventId);
            if (actual.Schedule == initial) throw;
            return new(actual, false);
        }
        return new(PublishChanged(committed), true);
    }

    public ServerEventState Schedule(ushort eventId, DateTimeOffset start, DateTimeOffset end)
    {
        RequireRegistered(eventId);
        if (Definition(eventId).SupportsSchedule != true)
            throw new InvalidDataException("该活动只支持开关，不支持设置起止时间");
        return Update(eventId,
            value => value with { StartsAt = start.ToUnixTimeSeconds(), EndsAt = end.ToUnixTimeSeconds(), OverrideActive = null });
    }

    ServerEventState Update(ushort eventId, Func<ServerEventSchedule, ServerEventSchedule> edit)
    {
        lock (updates)
        {
            var before = Read(eventId).Schedule;
            var desired = edit(before);
            if (desired.OverrideActive != false) RequireBattlePassPrepared(eventId);
            desired.Validate();
            var committed = store.Save(before, desired);
            return committed != before ? PublishChanged(committed) : State(committed);
        }
    }

    ServerEventState PublishChanged(ServerEventSchedule schedule)
    {
        ushort eventId = schedule.EventId;
        if (eventId == BattlePassId) Volatile.Write(ref battlePassSchedule, schedule);
        if (eventId == EndlessAdventurePolicy.EventId) Volatile.Write(ref endlessAdventureSchedule, schedule);
        if (eventId == Strongway2019Id) Interlocked.Increment(ref strongway2019DisplayRevision);
        var state = State(schedule);
        if (eventId == DailyAttendanceId) ObserveDailyAttendance(state);
        Changed?.Invoke(new(state, time.GetUtcNow(), time.GetTimestamp()));
        return state;
    }

    ServerEventState State(ServerEventSchedule schedule) => new(schedule,
        schedule.EventId == BattlePassId ? battlePassMissionsPrepared && IsBattlePassActive(time.GetUtcNow()) :
        schedule.EventId == NolanVisibilityId ? schedule.OverrideActive ?? true : schedule.IsActive(time.GetUtcNow()));

    public static void RequireRegistered(ushort eventId) => _ = Definition(eventId);
}
