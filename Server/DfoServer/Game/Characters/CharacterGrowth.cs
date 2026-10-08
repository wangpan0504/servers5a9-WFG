using DfoServer.GameWorld.Characters;
using DfoServer.Game.Skills;

namespace DfoServer.Game.Characters;

internal enum CharacterGrowthFailure
{
    None,
    InvalidSnapshot,
    InvalidTarget,
    Unsupported,
    PointOverflow
}

internal sealed record CharacterGrowthResult(CharacterGrowthFailure Failure, CharacterGrowthPlan? Plan)
{
    public bool Success => Failure == CharacterGrowthFailure.None;
}

// 只描述成长候选，不证明经验来源、事件去重、owner有效或事务已提交。
internal sealed class CharacterGrowthPlan
{
    private CharacterGrowthPlan(Character expected, byte targetLevel, CharacterLearningState learning, int spDelta)
    {
        CharacterId = expected.Id;
        Revision = expected.Revision;
        Job = expected.Job;
        GrowType = expected.GrowType;
        PreviousLevel = expected.Level;
        Before = Freeze(expected.Learning);
        TargetLevel = targetLevel;
        Learning = learning;
        LevelSpDelta = spDelta;
    }

    public ushort CharacterId { get; }
    public long Revision { get; }
    public byte Job { get; }
    public byte GrowType { get; }
    public byte PreviousLevel { get; }
    public CharacterLearningState Before { get; }
    public byte TargetLevel { get; }
    public CharacterLearningState Learning { get; }
    public int LevelSpDelta { get; }

    // 调用方提供已核验的奖励增量；累计等级表只决定升级边界，不决定奖励额度。
    public static CharacterGrowthResult AddExperience(Character expected, ulong amount, CharacterGrowthCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(catalog);
        if (expected.Learning is null)
            return Fail(CharacterGrowthFailure.InvalidSnapshot);
        if (amount == 0 || amount > ulong.MaxValue - expected.Learning.Experience)
            return Fail(CharacterGrowthFailure.InvalidTarget);
        ulong target = expected.Learning.Experience + amount;
        if (target >= catalog.GetCumulativeExperienceBoundary(CharacterGrowthCatalog.MaximumSupportedLevel))
            return Fail(CharacterGrowthFailure.InvalidTarget);
        byte level = 1;
        while (target >= catalog.GetCumulativeExperienceBoundary(level))
            level++;
        return Create(expected, level, target, catalog);
    }

    // 累计经验与等级来自调用方明确的权威候选；这里不生成击杀、任务或其他经验来源。
    public static CharacterGrowthResult Create(Character expected, byte targetLevel, ulong targetExperience,
        CharacterGrowthCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(catalog);
        if (expected.Job > 15 || expected.Level is < 1 or > CharacterGrowthCatalog.MaximumSupportedLevel ||
            expected.Learning is null || expected.Learning.Trees is null ||
            expected.Learning.Trees.Any(tree => tree is null || tree.Skills is null || tree.Skills.Any(skill => skill is null)))
            return Fail(CharacterGrowthFailure.InvalidSnapshot);
        try
        {
            expected.Learning.Validate();
        }
        catch (InvalidDataException)
        {
            return Fail(CharacterGrowthFailure.InvalidSnapshot);
        }

        if (!SkillPointLedger.SupportsGrowth(expected.GrowType, expected.Job) || !expected.Learning.Initialized)
            return Fail(CharacterGrowthFailure.Unsupported);
        if (!WithinLevel(expected.Level, expected.Learning.Experience))
            return Fail(CharacterGrowthFailure.InvalidSnapshot);
        if (targetLevel < expected.Level || targetLevel > CharacterGrowthCatalog.MaximumSupportedLevel ||
            targetExperience <= expected.Learning.Experience || !WithinLevel(targetLevel, targetExperience))
            return Fail(CharacterGrowthFailure.InvalidTarget);

        int spDelta = catalog.GetSpDelta(expected.Level, targetLevel);
        int tpDelta = catalog.GetTpDelta(expected.Level, targetLevel);
        if (expected.Learning.Trees.Any(tree => (int)tree.Sp + spDelta > ushort.MaxValue || (int)tree.Tp + tpDelta > ushort.MaxValue))
            return Fail(CharacterGrowthFailure.PointOverflow);
        // 余额加贡献差，保留已消费SP、两树不同余额、TP及所有稳定绑定。
        var trees = expected.Learning.Trees.OrderBy(tree => tree.Context).Select(tree => tree.Freeze()).Select(tree =>
            tree with { Sp = checked((ushort)(tree.Sp + spDelta)), Tp = checked((ushort)(tree.Tp + tpDelta)), Skills = Array.AsReadOnly(tree.Skills.ToArray()) }).ToArray();
        var learning = expected.Learning with { Experience = targetExperience, Trees = Array.AsReadOnly(trees) };
        if (catalog.Learning is { } skills && expected.Learning.BonusSp.HasValue)
            learning = SkillPointLedger.Synchronize(expected.Job, expected.GrowType, targetLevel, learning, skills,
                learning.ExpertContractExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? 5 : 0);
        return new(CharacterGrowthFailure.None, new(expected, targetLevel, learning, spDelta));

        bool WithinLevel(int level, ulong experience) => experience >= catalog.GetLevelStartExperience(level) &&
            experience < catalog.GetCumulativeExperienceBoundary(level);
    }

    private static CharacterLearningState Freeze(CharacterLearningState state) => state with { Trees =
        Array.AsReadOnly(state.Trees.OrderBy(tree => tree.Context).Select(tree =>
            tree.Freeze()).ToArray()) };

    private static CharacterGrowthResult Fail(CharacterGrowthFailure failure) => new(failure, null);
}
