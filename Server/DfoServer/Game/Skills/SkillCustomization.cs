using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Skills;

internal readonly record struct SkillCommandChange(ushort Id, string Command);

internal static class SkillCustomization
{
    // 尚未开放切树；当前客户端无context字段的编辑作用于主树。
    public static SkillLearningResult Commands(byte job, CharacterLearningState state,
        IReadOnlyList<SkillCommandChange> changes, bool reset, SkillLearningCatalog catalog)
    {
        if (!state.Initialized || changes.Select(change => change.Id).Distinct().Count() != changes.Count)
            return Fail();
        var tree = state.Trees.Single(tree => tree.Context == 0);
        foreach (var change in changes)
        {
            var resource = catalog.Get(job, change.Id);
            if (!tree.Skills.Any(skill => skill.Id == change.Id) || resource.Type != SkillType.Active || !resource.CommandCustomizing)
                return Fail();
        }
        var commands = changes.ToDictionary(change => change.Id, change => change.Command);
        var skills = tree.Skills.Select(skill => skill with { Command = reset ? "" : commands.GetValueOrDefault(skill.Id, skill.Command) }).ToArray();
        if (skills.SequenceEqual(tree.Skills)) return Success(state);
        var next = state with { Trees = Array.AsReadOnly(state.Trees.Select(value => value.Context == 0
            ? value with { Skills = Array.AsReadOnly(skills) } : value).ToArray()) };
        next.Validate();
        return Success(next);
    }

    public static SkillLearningResult Combo(byte job, CharacterLearningState state, byte context,
        IReadOnlyList<SkillCombo> combos, SkillLearningCatalog catalog, bool autoCombo = false)
    {
        if (job != 9 || !state.Initialized || context > 1 || combos.Count > 6 || combos.Select(combo => combo.Root).Distinct().Count() != combos.Count)
            return Fail();
        var tree = state.Trees.Single(tree => tree.Context == context);
        var roots = tree.Skills.Where(skill => catalog.Get(job, skill.Id).HasComboDeclaration).Select(skill => skill.Id).ToHashSet();
        if (combos.Any(combo => !roots.Contains(combo.Root) || combo.Children.Count > 6 || combo.Children.Any(id =>
            roots.Contains(id) || !tree.Skills.Any(skill => skill.Id == id) || catalog.Get(job, id).Type != SkillType.Active)))
            return Fail();
        // 客户端拖动跨组会在旧组保留同一ID；参考S4A21保留最后出现的位置。
        var last = new Dictionary<ushort, (int Root, int Child)>();
        for (int i = 0; i < combos.Count; i++)
            for (int j = 0; j < combos[i].Children.Count; j++) last[combos[i].Children[j]] = (i, j);
        var normalized = combos.Select((combo, i) => combo with { Children = Array.AsReadOnly(combo.Children
            .Where((id, j) => last[id] == (i, j)).ToArray()) }).ToList();
        foreach (var root in roots.Where(root => normalized.All(combo => combo.Root != root))) normalized.Add(new(root, Array.Empty<ushort>()));
        // 连招列顺序由根的快捷栏槽位决定，不能按技能ID重排。
        var slots = tree.Skills.ToDictionary(skill => skill.Id, skill => skill.Slot);
        var changed = tree with { Combos = Array.AsReadOnly(normalized.OrderBy(combo => slots[combo.Root]).ToArray()) };
        // 自动组合的排列由客户端本地生成并通过C2S508提交，服务端不做二次排序；
        // 客户端会把已收进连招列的上排技能从快捷栏移除，服务端按同一规则镜像，避免重登后图标复现。
        var skills = autoCombo ? AutoComboSkills(tree, combos) : tree.Skills;
        if (changed.SameCombos(tree) && ReferenceEquals(skills, tree.Skills)) return Success(state);
        var next = state with { Trees = Array.AsReadOnly(state.Trees.Select(value => value.Context == context
            ? changed with { Skills = skills } : value).ToArray()) };
        next.Validate();
        // 移出可见快捷栏后客户端本地仍保留旧图标；必须回放完整技能树才会移除。
        return autoCombo && !ReferenceEquals(skills, tree.Skills) ? Success(next) with { RequiresSynchronization = true } : Success(next);
    }

    // 510阶段先把会被收进任意连招列的上排技能移出快捷栏：客户端收到回执后才本地生成排列，
    // 服务端必须赶在它重建面板之前落库，否则重建出的旧槽位会覆盖刚排好的六列。
    public static SkillLearningResult PrepareAutoCombo(byte job, CharacterLearningState state, byte context,
        SkillLearningCatalog catalog)
    {
        if (job != 9 || !state.Initialized || context > 1) return Fail();
        var tree = state.Trees.Single(tree => tree.Context == context);
        var roots = tree.Skills.Where(skill => catalog.Get(job, skill.Id).HasComboDeclaration)
            .Select(skill => skill.Id).ToHashSet();
        // 客户端自动组合使用各根声明的子技能池，只保留当前树已学到的部分；
        // 未被学习的声明不占槽位，也不会被移动。
        var candidates = roots.SelectMany(root => catalog.Get(job, root).ComboSkills).ToHashSet();
        var skills = MoveOutOfQuickSlots(tree.Skills, id => candidates.Contains(id) && !roots.Contains(id));
        if (ReferenceEquals(skills, tree.Skills)) return Success(state);
        var next = state with { Trees = Array.AsReadOnly(state.Trees.Select(value => value.Context == context
            ? value with { Skills = skills } : value).ToArray()) };
        next.Validate();
        return Success(next) with { RequiresSynchronization = true };
    }

    // 只收走客户端本次排列实际包含的子技能；未进入排列的快捷技能保持原槽，
    // 因为客户端本地也只是把这批被收集的技能从上排移除。
    private static IReadOnlyList<LearnedSkill> AutoComboSkills(CharacterSkillTree tree, IReadOnlyList<SkillCombo> combos)
    {
        var collected = combos.SelectMany(combo => combo.Children).ToHashSet();
        return MoveOutOfQuickSlots(tree.Skills, collected.Contains);
    }

    // 只移动仍在可见快捷栏的技能；优先黑武扩展栏，扩展栏耗尽后退到普通槽，
    // 都没有空位时保留原绑定，不因自动排列丢技能。
    private static IReadOnlyList<LearnedSkill> MoveOutOfQuickSlots(IReadOnlyList<LearnedSkill> source,
        Func<ushort, bool> predicate)
    {
        var occupied = source.Select(skill => skill.Slot).ToHashSet();
        var skills = source.ToArray();
        bool moved = false;
        for (int i = 0; i < skills.Length; i++)
        {
            var skill = skills[i];
            if (!predicate(skill.Id) || !SkillSlotAllocator.QuickSlot(skill.Slot)) continue;
            int slot = SkillSlotAllocator.FirstFree(occupied, 150, 198);
            if (slot < 0) slot = SkillSlotAllocator.FirstFree(occupied, 12, 138);
            if (slot < 0) continue;
            occupied.Remove(skill.Slot);
            occupied.Add((byte)slot);
            skills[i] = skill with { Slot = (byte)slot };
            moved = true;
        }
        return moved ? Array.AsReadOnly(skills.OrderBy(skill => skill.Slot).ToArray()) : source;
    }

    private static SkillLearningResult Success(CharacterLearningState state) => new(SkillLearningFailure.None, state, Array.Empty<LearnedSkill>());
    private static SkillLearningResult Fail() => new(SkillLearningFailure.Unsupported, null, Array.Empty<LearnedSkill>());
}
