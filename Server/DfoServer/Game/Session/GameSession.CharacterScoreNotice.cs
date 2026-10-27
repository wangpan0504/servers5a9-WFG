using DfoServer.Game.Characters;

namespace DfoServer.Game.Session;

// Character只保留已选快照的引用身份；计算一律用另行冻结的Snapshot，不锁外读取活角色资产。
internal sealed record CharacterScoreNoticeCapture(string Account, long Generation, long SelectionEpoch, long Timestamp,
    Character Character, CharacterInvestmentSnapshot Snapshot);

internal sealed partial class GameSession
{
    internal const int ScoreNoticeLifetimeSeconds = 10;

    internal CharacterScoreNoticeCapture CaptureCharacterScoreNotice(string account, string name) => owner.Execute(() =>
    {
        if (!string.Equals(account, owner.Account, StringComparison.Ordinal) || !Ready || Selected is not { } selected ||
            !string.Equals(name, selected.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("评分提示仅限该账号当前就绪角色");
        long now = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
        return new CharacterScoreNoticeCapture(owner.Account, owner.Generation, SelectionEpoch,
            (timeProvider ?? TimeProvider.System).GetTimestamp(), selected,
            CharacterInvestmentScore.Capture(selected, now));
    });

    // 仅提供构造时的剩余缓存提示；发送泵仍另行核验真实身份/期限，入队不表示送达。
    internal int CharacterScoreRemainingMilliseconds(CharacterScoreNoticeCapture capture, long deadline)
    {
        var clock = timeProvider ?? TimeProvider.System;
        double elapsed = clock.GetElapsedTime(capture.Timestamp).TotalMilliseconds;
        double lifetime = Math.Min(ScoreNoticeLifetimeSeconds, deadline - capture.Snapshot.Equipment.ObservedAt) * 1000d;
        double remaining = Math.Min(lifetime - elapsed, deadline * 1000d - clock.GetUtcNow().ToUnixTimeMilliseconds());
        return elapsed < 0 || remaining < 1 ? 0 : (int)Math.Min(10000, Math.Floor(remaining));
    }

    // 发送泵已在同一owner内，Lease.Execute允许同账号重入；此处不做目录、SQL、网络或日志等待。
    internal string CharacterScoreNoticeStatus(CharacterScoreNoticeCapture capture, long deadline)
    {
        if (!owner.IsCurrent) return "stale_generation";
        try
        {
            return owner.Execute(() =>
            {
                if (capture.Account != owner.Account || capture.Generation != owner.Generation) return "stale_generation";
                if (!Ready || Selected is null) return "not_ready";
                if (SelectionEpoch != capture.SelectionEpoch || Selected.Id != capture.Snapshot.Equipment.CharacterId) return "selection_changed";
                if (Selected.Revision != capture.Snapshot.Equipment.CharacterRevision || !ReferenceEquals(Selected, capture.Character)) return "character_changed";
                var clock = timeProvider ?? TimeProvider.System;
                TimeSpan elapsed = clock.GetElapsedTime(capture.Timestamp);
                long lifetime = Math.Min(ScoreNoticeLifetimeSeconds, deadline - capture.Snapshot.Equipment.ObservedAt);
                if (lifetime <= 0 || elapsed < TimeSpan.Zero || elapsed >= TimeSpan.FromSeconds(lifetime)) return "expired";
                long now = clock.GetUtcNow().ToUnixTimeSeconds();
                return now < capture.Snapshot.Equipment.ObservedAt || now >= deadline ? "expired" : "ready";
            });
        }
        catch (InvalidOperationException error) when (error.Message == "Stale account generation")
        { return "stale_generation"; }
    }
}
