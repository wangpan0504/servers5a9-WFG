using DfoServer.GameWorld.Characters;

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

        // 沿用普通双树业务范围；特殊职业、转职及未初始化旧角色不自动补点或重建。
        if (expected.GrowType != 0 || expected.Job is 9 or 10 || !expected.Learning.Initialized)
            return Fail(CharacterGrowthFailure.Unsupported);
        if (!WithinLevel(expected.Level, expected.Learning.Experience))
            return Fail(CharacterGrowthFailure.InvalidSnapshot);
        if (targetLevel < expected.Level || targetLevel > CharacterGrowthCatalog.MaximumSupportedLevel ||
            targetExperience <= expected.Learning.Experience || !WithinLevel(targetLevel, targetExperience))
            return Fail(CharacterGrowthFailure.InvalidTarget);

        int spDelta = catalog.GetSpDelta(expected.Level, targetLevel);
        if (expected.Learning.Trees.Any(tree => (int)tree.Sp + spDelta > ushort.MaxValue))
            return Fail(CharacterGrowthFailure.PointOverflow);
        // 余额加贡献差，保留已消费SP、两树不同余额、TP及所有稳定绑定。
        var trees = expected.Learning.Trees.OrderBy(tree => tree.Context).Select(tree =>
            new CharacterSkillTree(tree.Context, checked((ushort)(tree.Sp + spDelta)), tree.Tp,
                Array.AsReadOnly(tree.Skills.ToArray()))).ToArray();
        var learning = new CharacterLearningState(targetExperience, Array.AsReadOnly(trees));
        return new(CharacterGrowthFailure.None, new(expected, targetLevel, learning, spDelta));

        bool WithinLevel(int level, ulong experience) => experience >= catalog.GetLevelStartExperience(level) &&
            experience < catalog.GetCumulativeExperienceBoundary(level);
    }

    private static CharacterLearningState Freeze(CharacterLearningState state) => new(state.Experience,
        Array.AsReadOnly(state.Trees.OrderBy(tree => tree.Context).Select(tree =>
            new CharacterSkillTree(tree.Context, tree.Sp, tree.Tp, Array.AsReadOnly(tree.Skills.ToArray()))).ToArray()));

    private static CharacterGrowthResult Fail(CharacterGrowthFailure failure) => new(failure, null);
}
