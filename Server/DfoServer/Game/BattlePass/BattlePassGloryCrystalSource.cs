using DfoServer.Game.Adventure;
using DfoServer.GameWorld.Adventure;
using DfoServer.GameWorld.BattlePass;

namespace DfoServer.Game.BattlePass;

// 荣耀结晶是原远征账户余额，只有到期领取的实际正差是取得事实。
internal static class BattlePassGloryCrystalSource
{
    public const string SourceKind = "expedition_glory_crystal_acquisition";

    public static bool Matches(byte category, BattlePassMission definition) => category == 1 &&
        definition is { Id: 39, RawTypeCode: 29, RawParameter: -1, RequiredCount: 20, Experience: 1000 };

    public static int Acquired(AdventureExpeditionPlan plan, AdventureExpedition run, AdventureCatalog catalog, DateTimeOffset now)
    {
        if (plan.Action != "expedition_claim") return 0;
        if (plan.ActorId == 0 || !plan.Before.Members.Any(member => member.Id == plan.ActorId) ||
            run.Id == Guid.Empty || run.Ends > now.ToUnixTimeSeconds() ||
            !plan.Before.State.Expeditions.Any(original => SameRun(original, run)) || plan.After.Expeditions.Any(row => row.Id == run.Id) ||
            plan.After.Expeditions.Length != plan.Before.State.Expeditions.Length - 1 ||
            plan.Before.State.Points.Length != 5 || plan.After.Points.Length != 5 ||
            !plan.Before.State.Points.Where((_, index) => index != 1).SequenceEqual(plan.After.Points.Where((_, index) => index != 1)))
            throw new InvalidDataException("荣耀结晶来源必须是原到期远征、账号成员及原领取计划");
        uint before = plan.Before.State.Points[1];
        ulong declared = checked((ulong)run.Reward + run.ExtraReward);
        uint maximum = catalog.Shops[1].Maximum;
        uint expected = checked((uint)Math.Min(maximum, checked((ulong)before + declared)));
        if (before > maximum || plan.After.Points[1] != expected)
            throw new InvalidDataException("荣耀结晶必须保留原冻结奖励与账户上限，只计实际到账正差");
        return checked((int)(expected - before));
    }

    // 事务内重读会重新分配ImmutableArray；原身份按完整内容比较，不依赖数组对象引用。
    static bool SameRun(AdventureExpedition original, AdventureExpedition current) =>
        original.Id == current.Id && original.Area == current.Area && original.Started == current.Started &&
        original.Ends == current.Ends && original.Reward == current.Reward && original.ExtraReward == current.ExtraReward &&
        original.Attributes.SequenceEqual(current.Attributes) && original.Members.SequenceEqual(current.Members) &&
        original.FrozenMembers.SequenceEqual(current.FrozenMembers);

    public static string SourceId(Guid run)
    {
        if (run == Guid.Empty) throw new InvalidDataException("荣耀结晶来源缺少原远征GUID");
        return run.ToString("N");
    }

    public static long NextCount(BattlePassMission definition, long current, int acquired)
    {
        if (!Matches(1, definition) || current < 0 || current > definition.RequiredCount || acquired <= 0)
            throw new InvalidDataException("荣耀结晶原任务或实际取得数量无效");
        return Math.Min(definition.RequiredCount, checked(current + acquired));
    }
}
