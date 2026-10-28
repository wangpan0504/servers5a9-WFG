using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal readonly record struct BossRushHud(byte Rank, int Score, int RemainMilliseconds);

internal sealed class BossRushRunState(BossRushCatalog catalog, int timeoutMilliseconds)
{
    readonly HashSet<int> scored = [];
    public BossRushCatalog Catalog { get; } = catalog;
    public int TimeoutMilliseconds { get; } = timeoutMilliseconds > 0 ? timeoutMilliseconds :
        throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
    public int Score { get; private set; }
    public byte Rank => Catalog.RankOf(Score);

    public bool TryScore(int monsterId)
    {
        int index = Catalog.IndexOf(monsterId);
        if (index < 0 || !scored.Add(monsterId)) return false;
        Score = checked(Score + Catalog.SoulFor(index));
        return true;
    }

    public int RemainMilliseconds(long startedTimestamp)
    {
        double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
        if (elapsed <= 0) return TimeoutMilliseconds;
        return elapsed >= TimeoutMilliseconds ? 0 : TimeoutMilliseconds - (int)elapsed;
    }

    public BossRushHud Hud(long startedTimestamp) => new(Rank, Score, RemainMilliseconds(startedTimestamp));
}
