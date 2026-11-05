using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 资源 [limit time] 首值必须是600，第二标量原样透传，不解释为延长上限。
// 本服倒计时按玩法要求使用1800秒；603首项发送该倒计时，不改写资源里的600。
internal sealed record LostLandAdmission
{
    public const int ResourceSeconds = 600;
    public const uint InitialSeconds = 1800;
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
            maze.ClearConditions is not [{ Type: "limit time", TargetId: ResourceSeconds, Count: > 0 } condition])
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
