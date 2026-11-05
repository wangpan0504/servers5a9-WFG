namespace DfoServer.GameWorld.Items;

// 重开活动仅取消这批道具的旧固定删除日；关活动不回收已经获得的道具。
internal static class EpicRoad2019ItemLifetime
{
    internal static readonly int OriginalDeadline = checked((int)new DateTimeOffset(2020, 1, 14, 6, 0, 0,
        TimeSpan.FromHours(8)).ToUnixTimeSeconds());

    internal static bool Applies(int id, long deadline) => deadline == OriginalDeadline && Includes(id);

    internal static bool Includes(int id) => id is
        490013716 or 490013717 or 490013718 or 490013719 or 490013720 or 490013721 or 490013722 or 490013723 or
        490013724 or 490013725 or 490013726 or 490013727 or 490013728 or 490013729 or 490013751 or 490013810;

    internal static ItemExpiration Restore(int id, ItemExpiration expiration) => Applies(id, expiration.AbsoluteTime)
        ? expiration with { AbsoluteTime = ItemExpiration.MaximumUnixTime } : expiration;
}
