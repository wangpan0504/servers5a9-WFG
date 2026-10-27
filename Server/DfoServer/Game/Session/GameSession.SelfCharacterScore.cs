namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal const int SelfScoreCooldownSeconds = 5;
    long? selfScoreLastStarted;
    CharacterScoreNoticeCapture? selfScorePending;

    // 只有本人当前角色，不接受客户端账号/角色参数；限频随会话存活，切角不清零。
    internal (CharacterScoreNoticeCapture? Capture, string Status) TryBeginSelfCharacterScore()
    {
        if (!owner.IsCurrent) return (null, "stale_generation");
        try
        {
            return owner.Execute<(CharacterScoreNoticeCapture?, string)>(() =>
            {
                if (!Ready || Selected is not { } selected) return (null, "not_ready");
                if (Volatile.Read(ref selfScorePending) is not null) return (null, "busy");
                var clock = timeProvider ?? TimeProvider.System;
                long stamp = clock.GetTimestamp();
                if (selfScoreLastStarted is { } previous && clock.GetElapsedTime(previous, stamp) < TimeSpan.FromSeconds(SelfScoreCooldownSeconds))
                    return (null, "throttled");
                var capture = CaptureCharacterScoreNotice(owner.Account, selected.Name);
                selfScoreLastStarted = stamp;
                Volatile.Write(ref selfScorePending, capture);
                return (capture, "ready");
            });
        }
        catch (InvalidOperationException error) when (error.Message == "Stale account generation")
        { return (null, "stale_generation"); }
    }

    // 即使账号已接管，也仅释放这次旧会话自己的引用，不查目录、不取得新owner。
    internal void EndSelfCharacterScore(CharacterScoreNoticeCapture capture) =>
        Interlocked.CompareExchange(ref selfScorePending, null, capture);
}
