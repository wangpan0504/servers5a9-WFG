using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 本服规则：初始挑战600秒；第二标量按匹配资源透传，不解释为延长上限。
internal sealed record LostLandAdmission
{
    public const uint InitialSeconds = 600;
    public uint OpaqueValue { get; }

    public LostLandAdmission(uint opaqueValue)
    {
        if (opaqueValue == 0) throw new ArgumentOutOfRangeException(nameof(opaqueValue));
        OpaqueValue = opaqueValue;
    }

    public static bool TryCreate(DungeonDefinition definition, DungeonMaze maze, out LostLandAdmission? admission)
    {
        admission = null;
        if (definition.Id != 4000 || definition.DungeonType != "lost land" || definition.Mazes.Count != 1 ||
            !definition.DisableFatigueConsumption || !definition.DisableGiveupPenalty ||
            !maze.IsLostLand || maze.ResourceError is not null || !maze.StaticTopology.Supported ||
            maze.ClearConditions is not [{ Type: "limit time", TargetId: (int)InitialSeconds, Count: > 0 } condition])
            return false;
        admission = new(checked((uint)condition.Count));
        return true;
    }
}

// 状态归实际正式实例；只用单调时钟，不创建线程、任务或持久化影子状态。
internal sealed class LostLandRunClock(LostLandAdmission admission, TimeProvider clock)
{
    long? started;
    public LostLandAdmission Admission { get; } = admission;
    public bool Started => started.HasValue;
    public bool Elapsed => Started && Remaining == TimeSpan.Zero;
    public bool Expired { get; private set; }
    public TimeSpan Remaining
    {
        get
        {
            var remaining = TimeSpan.FromSeconds(LostLandAdmission.InitialSeconds) -
                (started is { } at ? clock.GetElapsedTime(at) : TimeSpan.Zero);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public bool Start()
    {
        if (Started || Expired) return false;
        started = clock.GetTimestamp();
        return true;
    }

    public bool TryExpire()
    {
        if (!Started || Expired || Remaining > TimeSpan.Zero) return false;
        Expired = true;
        return true;
    }
}

// 跨正常过房和资产revision保留实例身份；不凭账号名寻找后来的新会话。
internal sealed record LostLandTimerReceipt(long Generation, long SelectionEpoch, Guid InstanceId,
    uint OpaqueValue, TimeSpan Remaining, bool Expired);
