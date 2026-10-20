namespace DfoServer.GameWorld.Dungeons;

internal enum ExclusiveNotice
{
    Death,
    Discard,
    ClearCard
}

// 这份客户端的静态结果没有把 particularhellparty.etc 的物品接到 DIE_MONSTER、DROP_ITEM 或 CLEAR_DUNGEON_REWARD。
// ParticularHellTriggerProven 保持 false 时，三条通知都不追加这张表。
internal static class ExclusiveTableNotice
{
    public static bool ParticularHellTriggerProven => false;

    public static IReadOnlyList<int> ParticularHellItemIds(int? enteredDungeonId)
    {
        if (!ParticularHellTriggerProven || enteredDungeonId is null)
            return [];
        throw new InvalidOperationException("专属掉落触发已标成已证明，但还没有接到现有通知");
    }

    public static void RejectUnproven(ExclusiveNotice notice, int? enteredDungeonId)
    {
        if (ParticularHellItemIds(enteredDungeonId).Count != 0 || ClearRewardSections.ExtraItemGrants().Count != 0)
            throw new InvalidOperationException("未证明的专属奖励不能进入 " + notice);
    }
}
