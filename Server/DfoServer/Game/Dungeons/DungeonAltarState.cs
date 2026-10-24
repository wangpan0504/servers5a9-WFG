using System.Collections.Immutable;

namespace DfoServer.Game.Dungeons;

// 由正式实例房间持有，所有调用在原账号owner内执行。死亡仍归Run，计时器不保存第二份死亡集合。
internal sealed class DungeonAltarState(DungeonAltarPlan plan)
{
    readonly HashSet<ushort> published = [];
    DungeonAltarRound? active;
    long roundStarted;
    int nextBatch;
    public DungeonAltarPlan Plan { get; } = plan;
    public int Round { get; private set; }
    public bool Started { get; private set; }
    public bool UiPrepared { get; private set; }
    public void PrepareUi() => UiPrepared = true;
    public bool Completed { get; private set; }
    public long? ChoiceDue { get; private set; }
    public bool WaitingForDifficulty => Started && !Completed && active is null && Plan.Definition.Ultimate;
    public byte Difficulty => active?.Difficulty ?? 0;
    public long? NextDue => active is not null && nextBatch < active.Batches.Length
        ? checked(roundStarted + active.Batches[nextBatch].DelayMilliseconds) : null;
    public ImmutableArray<byte> Choices => Plan.Rounds.Where(row => row.Round == Round).Select(row => row.Difficulty).ToImmutableArray();

    public bool Begin(long now, byte difficulty)
    {
        if (now < 0 || Completed || active is not null || Started && !WaitingForDifficulty) return false;
        var selected = Plan.Rounds.SingleOrDefault(row => row.Round == Round && row.Difficulty == difficulty);
        if (selected is null) return false;
        active = selected;
        ChoiceDue = null;
        Started = true;
        nextBatch = 0;
        roundStarted = now;
        return true;
    }

    public DungeonAltarBatch? TakeDue(long now)
    {
        if (NextDue is not { } due || now < due) return null;
        var batch = active!.Batches[nextBatch++];
        foreach (var actor in batch.Actors)
            if (!published.Add(actor.ObjectKey)) throw new InvalidOperationException("祭坛对象重复生成");
        return batch;
    }

    public bool WasPublished(ushort key) => published.Contains(key);
    public bool CanComplete(IReadOnlySet<ushort> dead, ushort? pending = null) => !Completed &&
        active is not null && Round == Plan.Definition.RoundCount - 1 && nextBatch == active.Batches.Length &&
        published.All(key => dead.Contains(key) || key == pending);

    public bool Advance(IReadOnlySet<ushort> dead, long now)
    {
        if (active is null || nextBatch != active.Batches.Length || published.Any(key => !dead.Contains(key))) return false;
        active = null;
        Round++;
        if (Round == Plan.Definition.RoundCount) { Completed = true; return true; }
        if (Plan.Definition.Ultimate)
            ChoiceDue = checked(now + (Round == Plan.Definition.RoundCount - 1 ? 1000 : 10000));
        if (!Plan.Definition.Ultimate)
        {
            // 无尽下一回合直接开始；极限保留选择窗口，第一和末回合的唯一A由调用方按资源启动。
            active = Plan.Rounds.Single(row => row.Round == Round && row.Difficulty == 0);
            nextBatch = 0;
            roundStarted = now;
        }
        return true;
    }
}
