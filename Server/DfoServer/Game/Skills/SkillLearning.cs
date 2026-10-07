using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Skills;

internal enum SkillLearningFailure
{
    None,
    InvalidRequest,
    Unsupported,
    MissingSkill,
    InsufficientSp,
    Mastered,
    RequiredLevel,
    Prerequisite,
    Capacity
}

internal sealed record SkillLearningChangeRequest(ushort Id, bool Decrease, byte Magnitude);
internal sealed record SkillLearningCommand(byte Context, byte Mode, IReadOnlyList<SkillLearningChangeRequest> Entries);
internal sealed record SkillLearningReceipt(Character Character, long Generation, long SelectionEpoch);
internal sealed record SkillLearningResult(SkillLearningFailure Failure, CharacterLearningState? State, IReadOnlyList<LearnedSkill> Changes)
{
    public bool Success => Failure == SkillLearningFailure.None;
    public SkillLearningReceipt? Receipt { get; init; }
}

internal static class SkillLearning
{
    public static SkillLearningResult Initialize(Character character, Func<byte, ushort, SkillLearningDefinition> definition)
    {
        if (character.Learning.Initialized)
            return new(SkillLearningFailure.None, character.Learning, EmptyChanges());
        if (character.Skills.Length == 0)
            return Fail(SkillLearningFailure.Unsupported);
        var result = Initialize(character.Job, character.Skills, definition);
        return result.Success ? result with { State = result.State! with { Experience = character.Learning.Experience } } : result;
    }

    public static SkillLearningResult Initialize(byte job, IReadOnlyList<CharacterSkill> initial, SkillLearningCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Initialize(job, initial, catalog.Get);
    }

    // 独立定义输入也用于边界测试；生产调用由 Catalog 提供匹配职业资源。
    internal static SkillLearningResult Initialize(byte job, IReadOnlyList<CharacterSkill> initial,
        Func<byte, ushort, SkillLearningDefinition> definition)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(definition);
        if (job > 15 || initial.Count > 255 || initial.Any(skill => skill is null || skill.Level == 0) ||
            initial.Select(skill => skill.Id).Distinct().Count() != initial.Count)
            return Fail(SkillLearningFailure.InvalidRequest);
        if (job is 9 or 10)
            return Fail(SkillLearningFailure.Unsupported);
        var bindings = new List<LearnedSkill>();
        foreach (CharacterSkill skill in initial)
        {
            SkillLearningDefinition resource;
            try
            {
                resource = definition(job, skill.Id);
            }
            catch (KeyNotFoundException)
            {
                return Fail(SkillLearningFailure.MissingSkill);
            }

            // combo 只有六槽容量已确认，槽位选择规则未闭合，不能用普通空槽冒充。
            if (resource.Job != job || resource.SkillId != skill.Id || resource.HasComboDeclaration)
                return Fail(SkillLearningFailure.Unsupported);
            // CHR 已有绑定不要求可继续学习；固有已精通技能可缺少费用/等级间隔声明。
            bindings.Add(new(checked((byte)bindings.Count), skill.Id, skill.Level));
        }

        CharacterSkillTree[] trees =
        [
            new(0, 0, 0, Array.AsReadOnly(bindings.ToArray())),
            new(1, 0, 0, Array.AsReadOnly(bindings.Select(skill => skill with { }).ToArray()))
        ];
        return new(SkillLearningFailure.None, new(0, Array.AsReadOnly(trees)), EmptyChanges());
    }

    public static SkillLearningResult Plan(byte job, byte growtype, byte characterLevel, CharacterLearningState current,
        SkillLearningCommand command, SkillLearningCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Plan(job, growtype, characterLevel, current, command, catalog.Get);
    }

    internal static SkillLearningResult Plan(byte job, byte growtype, byte characterLevel, CharacterLearningState current,
        SkillLearningCommand command, Func<byte, ushort, SkillLearningDefinition> definition)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(definition);
        if (job > 15 || characterLevel is < 1 or > 95 || command.Context > 1 || command.Mode > 1 ||
            command.Entries is null || command.Entries.Count is < 1 or > 255 ||
            command.Entries.Any(entry => entry is null || entry.Decrease || entry.Magnitude != 1) ||
            command.Entries.Select(entry => entry.Id).Distinct().Count() != command.Entries.Count)
            return Fail(SkillLearningFailure.InvalidRequest);
        if (growtype != 0 || job is 9 or 10 || !current.Initialized)
            return Fail(SkillLearningFailure.Unsupported);
        try
        {
            current.Validate();
        }
        catch (InvalidDataException)
        {
            return Fail(SkillLearningFailure.InvalidRequest);
        }

        CharacterSkillTree source = current.Trees.Single(tree => tree.Context == command.Context);
        var working = source.Skills.ToDictionary(skill => skill.Id);
        var changes = new List<LearnedSkill>();
        int sp = source.Sp;
        foreach (SkillLearningChangeRequest entry in command.Entries)
        {
            SkillLearningDefinition resource;
            try
            {
                resource = definition(job, entry.Id);
            }
            catch (KeyNotFoundException)
            {
                return Fail(SkillLearningFailure.MissingSkill);
            }

            if (resource.Job != job || resource.SkillId != entry.Id || !resource.SupportsOrdinarySpLearning)
                return Fail(SkillLearningFailure.Unsupported);
            working.TryGetValue(entry.Id, out LearnedSkill? learned);
            int level = learned?.Level ?? 0;
            // 当前仅 grow0；二转 OR 上限和其他职业状态不参与猜测。
            int maximum = resource.GetDeclaredGrowtypeMaximum(0);
            if (level >= maximum || level == byte.MaxValue)
                return Fail(SkillLearningFailure.Unsupported);
            if (resource.PointCostLevelCount is not int costCount || level >= costCount ||
                resource.DeclaredMaximumLevels is null || level >= resource.DeclaredMaximumLevels.Max())
                return Fail(SkillLearningFailure.Unsupported);
            if (characterLevel < resource.GetOrdinaryNextRequiredCharacterLevel(level))
                return Fail(SkillLearningFailure.RequiredLevel);
            foreach (SkillPrerequisite prerequisite in resource.Prerequisites!)
            {
                if (prerequisite.Level == 0 && !working.ContainsKey(prerequisite.SkillId))
                {
                    try
                    {
                        var prerequisiteDefinition = definition(job, prerequisite.SkillId);
                        if (prerequisiteDefinition.Job != job || prerequisiteDefinition.SkillId != prerequisite.SkillId)
                            return Fail(SkillLearningFailure.Unsupported);
                    }
                    catch (KeyNotFoundException)
                    {
                        return Fail(SkillLearningFailure.Unsupported);
                    }
                    // 客户端惰性构造存在资源的未学对象，实际等级零可满足所需零。
                    continue;
                }
                if (!working.TryGetValue(prerequisite.SkillId, out LearnedSkill? required) || required.Level < prerequisite.Level)
                    return Fail(SkillLearningFailure.Prerequisite);
            }

            // 已确认资源百分比截断；角色 +1100 减免状态当前明确为零，不引入额外状态。
            int cost = resource.GetOrdinaryResourceSpCost(level, 0);
            if (cost > sp)
                return Fail(SkillLearningFailure.InsufficientSp);
            byte slot;
            if (learned is not null)
            {
                slot = learned.Slot;
            }
            else
            {
                var used = working.Values.Select(skill => skill.Slot).ToHashSet();
                int vacant = Enumerable.Range(0, 255).FirstOrDefault(index => !used.Contains((byte)index), -1);
                if (vacant < 0)
                    return Fail(SkillLearningFailure.Capacity);
                slot = (byte)vacant;
            }

            sp -= cost;
            var next = new LearnedSkill(slot, entry.Id, checked((byte)(level + 1)));
            working[entry.Id] = next;
            changes.Add(next);
        }

        var trees = current.Trees.OrderBy(tree => tree.Context).Select(tree => tree.Context == command.Context
            ? new CharacterSkillTree(tree.Context, checked((ushort)sp), tree.Tp,
                Array.AsReadOnly(working.Values.OrderBy(skill => skill.Slot).ToArray()))
            : new CharacterSkillTree(tree.Context, tree.Sp, tree.Tp, Array.AsReadOnly(tree.Skills.ToArray()))).ToArray();
        var state = new CharacterLearningState(current.Experience, Array.AsReadOnly(trees));
        state.Validate();
        return new(SkillLearningFailure.None, state, Array.AsReadOnly(changes.ToArray()));
    }

    private static IReadOnlyList<LearnedSkill> EmptyChanges() => Array.AsReadOnly(Array.Empty<LearnedSkill>());
    private static SkillLearningResult Fail(SkillLearningFailure failure) => new(failure, null, EmptyChanges());
}
