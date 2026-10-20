namespace DfoServer.Game.Chat;

/// <summary>
/// 每连接聊天节流：同一连接 1 秒最多 5 条、10 秒最多 30 条，超出只丢弃（不断线）。
/// 用最近 30 条被接受消息的单调时间戳实现，窗口比较走 <see cref="TimeProvider.GetTimestamp"/>，
/// 不受系统时钟跳变影响；每次判定最多扫描 30 个 <see cref="long"/>，不分配临时集合。
/// </summary>
internal sealed class ChatRateLimiter(TimeProvider? clock = null)
{
    public const int ShortWindowLimit = 5;
    public const int LongWindowLimit = 30;
    static readonly TimeSpan ShortWindow = TimeSpan.FromSeconds(1);
    static readonly TimeSpan LongWindow = TimeSpan.FromSeconds(10);

    readonly TimeProvider clock = clock ?? TimeProvider.System;
    // 按下标保存已接受消息的时间戳；每次判定先原地丢弃 10 秒外的记录，因此长度不会超过上限。
    readonly long[] accepted = new long[LongWindowLimit];
    int count;

    /// <summary>允许本次发送时返回 true 并记录时间戳；命中任一窗口时返回 false 且不记录。</summary>
    public bool Allow()
    {
        long now = clock.GetTimestamp();
        int live = 0, recent = 0;
        for (int index = 0; index < count; index++)
        {
            long stamp = accepted[index];
            if (clock.GetElapsedTime(stamp, now) >= LongWindow)
                continue;
            accepted[live++] = stamp;
            if (clock.GetElapsedTime(stamp, now) < ShortWindow)
                recent++;
        }

        count = live;
        if (live >= LongWindowLimit || recent >= ShortWindowLimit)
            return false;
        accepted[count++] = now;
        return true;
    }
}
