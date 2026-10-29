namespace DfoServer.Game.Dungeons;

internal enum BeastTerminal { Active, Cleared, TimedOut, Abandoned }
internal sealed record BeastStageScore(int Stage, long FinishedAt, int Seconds, int Score);
internal sealed record BeastProgressView(Guid InstanceId, int Stage, int CompletedStages, int Score, byte Rank,
    int RemainingMilliseconds, BeastTerminal Terminal, IReadOnlyList<BeastStageScore> Stages);

// 单人归原会话、组队归共享实例；各角色Run只维护自己的死亡及资产账本。
// 时钟不建立线程或任务，网络层只能租用一个可转移的到期调度资格。
internal sealed class BeastRunProgress(Guid instanceId, TimeProvider clock)
{
    readonly object gate = new();
    readonly List<BeastStageScore> stages = [];
    int stage = -1;
    long stageStarted;
    long? countdownStarted;
    BeastStageScore? pending;
    BeastTerminal terminal;
    BeastDeadlineLease? schedule;

    public long Timestamp => clock.GetTimestamp();

    public bool Enter(int next, long? acceptedAt = null)
    {
        lock (gate)
        {
            if (terminal != BeastTerminal.Active || next is < 0 or > 5) return false;
            if (next == stage) return true;
            long now = acceptedAt ?? clock.GetTimestamp();
            if (next != stage + 1 || next != stages.Count || pending is not null || RemainingAt(now) == 0)
                return false;
            stage = next;
            stageStarted = now;
            if (stage == 2) countdownStarted = stageStarted;
            return true;
        }
    }

    public bool CanObserveDeath
    {
        get => CanObserveAt(clock.GetTimestamp());
    }

    public bool CanObserveAt(long at)
    { lock (gate) return terminal == BeastTerminal.Active && (pending is not null || RemainingAt(at) != 0); }

    public BeastStageScore? Prepare(int expectedStage, long? acceptedAt = null)
    {
        lock (gate)
        {
            if (terminal != BeastTerminal.Active || expectedStage != stage || stage != stages.Count) return null;
            if (pending is not null) return pending;
            long now = acceptedAt ?? clock.GetTimestamp();
            if (RemainingAt(now) == 0) return null;
            int seconds = checked((int)Math.Clamp(clock.GetElapsedTime(stageStarted, now).TotalSeconds, 1, int.MaxValue));
            return pending = new(stage, now, seconds, StageScore(stage, seconds));
        }
    }

    public bool Confirm(BeastStageScore expected)
    {
        lock (gate)
        {
            if (stages.Any(row => ReferenceEquals(row, expected))) return true;
            if (terminal != BeastTerminal.Active || !ReferenceEquals(pending, expected)) return false;
            stages.Add(expected);
            pending = null;
            if (stages.Count == 6) Finish(BeastTerminal.Cleared);
            return true;
        }
    }

    public bool TryExpire()
    {
        lock (gate)
        {
            // 已接受的死亡必须先完成原事务；不能让超时覆盖已冻结的压线击杀。
            if (terminal != BeastTerminal.Active || pending is not null || RemainingAt(clock.GetTimestamp()) != 0) return false;
            Finish(BeastTerminal.TimedOut);
            return true;
        }
    }

    public BeastProgressView View()
    {
        lock (gate)
        {
            int score = stages.Sum(row => row.Score);
            return new(instanceId, stage, stages.Count, score, Rank(score), RemainingAt(clock.GetTimestamp()), terminal, stages.ToArray());
        }
    }

    int RemainingAt(long now) => countdownStarted is not { } start ? -1 :
        checked((int)Math.Clamp(Math.Ceiling((TimeSpan.FromSeconds(180) - clock.GetElapsedTime(start, now)).TotalMilliseconds), 0, 180000));

    public BeastDeadlineLease? TrySchedule(ushort character)
    {
        lock (gate)
        {
            if (schedule is not null || countdownStarted is null || terminal != BeastTerminal.Active) return null;
            return schedule = new(character);
        }
    }

    public void Release(BeastDeadlineLease expected)
    {
        lock (gate)
        {
            if (!ReferenceEquals(schedule, expected)) return;
            schedule = null;
            expected.Cancel();
        }
    }

    public void RemoveParticipant(ushort character, bool last)
    {
        lock (gate)
        {
            if (schedule?.Character == character) Release(schedule);
            if (last && terminal == BeastTerminal.Active) Finish(BeastTerminal.Abandoned);
        }
    }

    void Finish(BeastTerminal result)
    {
        terminal = result;
        if (schedule is not null) Release(schedule);
    }

    public static int StageScore(int stage, int seconds)
    {
        if (stage is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(stage));
        int tier = seconds <= 60 ? 0 : seconds <= 120 ? 1 : seconds <= 240 ? 2 : seconds <= 460 ? 3 :
            seconds <= 580 ? 4 : seconds <= 700 ? 5 : 6;
        int[] points = stage == 0 ? [1000,900,800,700,600,500,400] :
            stage == 1 ? [2000,1900,1800,1700,1600,1500,1400] : [3000,2800,2600,2400,2200,2000,1800];
        int multiplier = (tier < 3 ? 13 : tier < 6 ? 12 : 11) + (stage < 2 ? 0 : 1);
        return points[tier] * multiplier / 10;
    }

    public static byte Rank(int score) => score >= 16000 ? (byte)0 : score >= 14000 ? (byte)1 :
        score >= 10000 ? (byte)2 : score >= 8000 ? (byte)3 : score >= 5000 ? (byte)4 : score >= 3000 ? (byte)5 : (byte)6;
}

internal sealed class BeastDeadlineLease(ushort character) : IDisposable
{
    readonly CancellationTokenSource cancellation = new();
    public ushort Character { get; } = character;
    public CancellationToken Stop => cancellation.Token;
    internal void Cancel() => cancellation.Cancel();
    public void Dispose() => cancellation.Dispose();
}
