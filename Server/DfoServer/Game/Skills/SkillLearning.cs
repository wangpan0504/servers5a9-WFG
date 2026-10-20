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
    InsufficientTp,
    Mastered,
    RequiredLevel,
    Prerequisite,
    Capacity
}

internal sealed record SkillLearningChangeRequest(ushort Id, bool Decrease, byte Magnitude);
internal sealed record SkillLearningCommand(byte Context, byte Mode, IReadOnlyList<SkillLearningChangeRequest> Entries);
internal sealed record SkillLearningReceipt(Character Character, long Generation, long SelectionEpoch)
{
    public DfoServer.Game.Session.SessionPhase Phase { get; init; } = DfoServer.Game.Session.SessionPhase.Town;
    public SkillSwitchInventoryState PreviousSkillSwitchInventory { get; init; } = Character.SkillSwitchInventory;
}
internal sealed record SkillLearningResult(SkillLearningFailure Failure, CharacterLearningState? State, IReadOnlyList<LearnedSkill> Changes)
{
    public bool Success => Failure == SkillLearningFailure.None;
    public SkillLearningReceipt? Receipt { get; init; }
    public bool RequiresSynchronization { get; init; }
    public int? TotalSp { get; init; }
    public int? TotalTp { get; init; }
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
        return result.Success ? result with { State = result.State! with
        {
            Experience = character.Learning.Experience,
            BonusSp = character.Level == 1 ? 0 : null,
            BonusTp = character.Level == 1 ? 0 : null,
            ExpertContractExpiresAt = character.Learning.ExpertContractExpiresAt
        } } : result;
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
        if (initial.Count == 0) return Fail(SkillLearningFailure.Unsupported);
        var bindings = new List<LearnedSkill>();
        var occupied = new HashSet<byte>();
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

            // 黑暗武士的连招根来自CHR，子技能独立保留等级和绑定。
            if (resource.Job != job || resource.SkillId != skill.Id || resource.HasComboDeclaration && job != 9 ||
                resource.Type is not (SkillType.Active or SkillType.Passive))
                return Fail(SkillLearningFailure.Unsupported);
            // CHR 已有绑定不要求可继续学习；固有已精通技能可缺少费用/等级间隔声明。
            int slot = SkillSlotAllocator.Allocate(resource, occupied, initial: true);
            if (slot < 0)
                return Fail(SkillLearningFailure.Capacity);
            occupied.Add((byte)slot);
            bindings.Add(new((byte)slot, skill.Id, skill.Level));
        }

        // 初始连招列同样跟随根的快捷栏槽位，与NOTI19绑定顺序一致。
        var combos = bindings.Where(skill => definition(job, skill.Id).HasComboDeclaration).OrderBy(skill => skill.Slot).Select(skill =>
            new SkillCombo(skill.Id, Array.AsReadOnly(definition(job, skill.Id).ComboSkills.Where(id => bindings.Any(value => value.Id == id)).ToArray()))).ToArray();
        LearnedSkill[] entries = bindings.OrderBy(skill => skill.Slot).ToArray();
        CharacterSkillTree[] trees =
        [
            new(0, 0, 0, Array.AsReadOnly(entries)) { Combos = Array.AsReadOnly(combos) },
            new(1, 0, 0, Array.AsReadOnly(entries.Select(skill => skill with { }).ToArray())) { Combos = Array.AsReadOnly(combos) }
        ];
        return new(SkillLearningFailure.None, new(0, Array.AsReadOnly(trees)) { BonusSp = 0, BonusTp = 0 }, EmptyChanges());
    }

    public static SkillLearningResult Plan(byte job, byte growtype, byte characterLevel, CharacterLearningState current,
        SkillLearningCommand command, SkillLearningCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!SkillPointLedger.SupportsGrowth(growtype, job) || !current.Initialized)
            return Fail(SkillLearningFailure.Unsupported);
        try
        {
            int contractLevels = catalog.ActiveExpertContractLevels(current.ExpertContractExpiresAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var synchronized = SkillPointLedger.Synchronize(job, growtype, characterLevel, current, catalog, contractLevels);
            var result = Plan(job, growtype, characterLevel, synchronized, command, catalog.Get,
                SkillPointLedger.FreeBaseline(job, growtype, characterLevel, catalog, contractLevels),
                synchronized.BonusSp is int bonus ? checked(catalog.TotalSp(characterLevel) + bonus) : null, contractLevels, synchronized.BonusTp is int tpBonus ? checked(catalog.TotalTp(characterLevel) + tpBonus) : null);
            return result with { RequiresSynchronization = synchronized.Trees.Zip(current.Trees).Any(pair =>
                pair.First.Sp != pair.Second.Sp || pair.First.Tp != pair.Second.Tp || !pair.First.Skills.SequenceEqual(pair.Second.Skills)) };
        }
        catch (NotSupportedException) { return Fail(SkillLearningFailure.Unsupported); }
        catch (KeyNotFoundException) { return Fail(SkillLearningFailure.Unsupported); }
    }

    internal static SkillLearningResult Plan(byte job, byte growtype, byte characterLevel, CharacterLearningState current,
        SkillLearningCommand command, Func<byte, ushort, SkillLearningDefinition> definition,
        IReadOnlyDictionary<ushort, byte>? free = null, int? totalSp = null, int contractLevels = 0, int? totalTp = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(definition);
        if (job > 15 || characterLevel is < 1 or > 95 || command.Context > 1 || command.Mode > 1 ||
            command.Entries is null || command.Entries.Count is < 1 or > 255 ||
            command.Entries.Any(entry => entry is null || entry.Magnitude == 0) ||
            command.Entries.Select(entry => entry.Id).Distinct().Count() != command.Entries.Count)
            return Fail(SkillLearningFailure.InvalidRequest);
        if (!SkillPointLedger.SupportsGrowth(growtype, job) || !current.Initialized)
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
        int sp = totalSp is int initialTotal ? Math.Max(0, initialTotal - SkillPointLedger.Spent(working.Values, free!, growtype, id => definition(job, id))) : source.Sp;
        int tp = totalTp is int initialTp ? Math.Max(0, initialTp - SkillPointLedger.Spent(working.Values, free!, growtype, id => definition(job, id), tp: true)) : source.Tp;
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

            if (resource.Job != job || resource.SkillId != entry.Id || !resource.SupportsPointLearning)
                return Fail(SkillLearningFailure.Unsupported);
            working.TryGetValue(entry.Id, out LearnedSkill? learned);
            int level = learned?.Level ?? 0;
            if (resource.FixedLevel is not null)
                return Fail(SkillLearningFailure.Mastered);
            int maximum = Math.Min(resource.GetMaximum(growtype), resource.DeclaredMaximumLevels![growtype & 15]);
            int target = entry.Decrease ? Math.Min(level, Math.Max(free?.GetValueOrDefault(entry.Id) ?? 0, level - entry.Magnitude)) : level + entry.Magnitude;
            if (entry.Decrease && ((resource.IsFeatureSkill ? totalTp : totalSp) is null || learned is null))
                return Fail(SkillLearningFailure.Unsupported);
            if (target > maximum || target > byte.MaxValue || !entry.Decrease && target <= level)
                return Fail(SkillLearningFailure.Mastered);
            if (!entry.Decrease && (long)characterLevel + (resource.IsFeatureSkill ? 0 : contractLevels) < resource.GetOrdinaryNextRequiredCharacterLevel(target - 1))
                return Fail(SkillLearningFailure.RequiredLevel);
            foreach (SkillPrerequisite prerequisite in entry.Decrease ? [] : resource.Prerequisites!)
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
            int cost = 0;
            if (!entry.Decrease)
                for (int rank = level; rank < target; rank++)
                    cost = checked(cost + resource.GetPointCost(rank, growtype));
            if (cost > (resource.IsFeatureSkill ? tp : sp))
                return Fail(resource.IsFeatureSkill ? SkillLearningFailure.InsufficientTp : SkillLearningFailure.InsufficientSp);
            byte slot;
            if (learned is not null)
            {
                slot = learned.Slot;
            }
            else
            {
                if (resource.SlotGroup is null)
                    return Fail(SkillLearningFailure.Unsupported);
                var used = working.Values.Select(skill => skill.Slot).ToHashSet();
                // 同批前序退点释放的槽位仍要出现在本次成功回执的变更列表中，不能再分配给新技能，否则槽位重复。
                foreach (LearnedSkill change in changes)
                    if (change.Level == 0)
                        used.Add(change.Slot);
                int vacant = SkillSlotAllocator.Allocate(resource, used);
                if (vacant < 0)
                    return Fail(SkillLearningFailure.Capacity);
                slot = (byte)vacant;
            }

            if (resource.IsFeatureSkill) tp -= cost; else sp -= cost;
            var next = new LearnedSkill(slot, entry.Id, checked((byte)target)) { Command = learned?.Command ?? "" };
            if (target == 0) working.Remove(entry.Id);
            else working[entry.Id] = next;
            changes.Add(next);
            if (totalSp is int total)
                sp = Math.Max(0, total - SkillPointLedger.Spent(working.Values, free!, growtype, id => definition(job, id)));
            if (totalTp is int tpTotal)
                tp = Math.Max(0, tpTotal - SkillPointLedger.Spent(working.Values, free!, growtype, id => definition(job, id), tp: true));
        }

        // 先规划整批，再核对降级是否使仍保留的技能失去前置；失败不提交半批。
        if (command.Entries.Any(entry => entry.Decrease))
            foreach (var skill in working.Values)
            {
                if (skill.Level <= (free?.GetValueOrDefault(skill.Id) ?? 0)) continue;
                var prerequisites = definition(job, skill.Id).Prerequisites;
                if (prerequisites is not null && prerequisites.Any(required => required.Level > 0 &&
                    (!working.TryGetValue(required.SkillId, out var learned) || learned.Level < required.Level)))
                    return Fail(SkillLearningFailure.Prerequisite);
            }

        var trees = current.Trees.OrderBy(tree => tree.Context).Select(tree => tree.Context == command.Context
            ? tree with { Sp = checked((ushort)sp), Tp = checked((ushort)tp),
                Skills = Array.AsReadOnly(working.Values.OrderBy(skill => skill.Slot).ToArray()),
                Combos = Array.AsReadOnly(tree.Combos.Select(combo => combo with { Children = Array.AsReadOnly(combo.Children.Where(working.ContainsKey).ToArray()) }).ToArray()) }
            : tree.Freeze()).ToArray();
        var state = current with { Trees = Array.AsReadOnly(trees) };
        state.Validate();
        return new(SkillLearningFailure.None, state, Array.AsReadOnly(changes.ToArray())) { TotalSp = totalSp, TotalTp = totalTp };
    }

    private static IReadOnlyList<LearnedSkill> EmptyChanges() => Array.AsReadOnly(Array.Empty<LearnedSkill>());
    private static SkillLearningResult Fail(SkillLearningFailure failure) => new(failure, null, EmptyChanges());
}
