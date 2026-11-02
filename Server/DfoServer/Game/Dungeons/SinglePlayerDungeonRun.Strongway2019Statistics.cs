namespace DfoServer.Game.Dungeons;

internal sealed partial class SinglePlayerDungeonRun
{
    long? strongway2019ClearElapsedMilliseconds;
    // 只有真实ClearFact签发时才产生耗时；结算、返城和读历史均不能延长此值。
    internal long? Strongway2019ElapsedMilliseconds => strongway2019ClearElapsedMilliseconds;

    void FreezeStrongway2019Elapsed()
    {
        if (strongway2019Started is { } started)
            strongway2019ClearElapsedMilliseconds ??= Math.Max(0,
                strongway2019Clock.GetElapsedTime(started).Ticks / TimeSpan.TicksPerMillisecond);
    }
}
