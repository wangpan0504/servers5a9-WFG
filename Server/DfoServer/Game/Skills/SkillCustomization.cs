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
        IReadOnlyList<SkillCombo> combos, SkillLearningCatalog catalog)
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
        var changed = tree with { Combos = Array.AsReadOnly(normalized.OrderBy(combo => combo.Root).ToArray()) };
        if (changed.SameCombos(tree)) return Success(state);
        var next = state with { Trees = Array.AsReadOnly(state.Trees.Select(value => value.Context == context ? changed : value).ToArray()) };
        next.Validate();
        return Success(next);
    }

    public static SkillLearningResult ClearComboQuickSlots(byte job, CharacterLearningState state, byte context, SkillLearningCatalog catalog)
    {
        if (job != 9 || !state.Initialized || context > 1) return Fail();
        var tree = state.Trees.Single(tree => tree.Context == context);
        var occupied = tree.Skills.Select(skill => skill.Slot).ToHashSet();
        var skills = tree.Skills.ToArray();
        bool changed = false;
        for (int i = 0; i < skills.Length; i++)
        {
            var skill = skills[i];
            if (!SkillSlotAllocator.QuickSlot(skill.Slot)) continue;
            var resource = catalog.Get(job, skill.Id);
            if (resource.HasComboDeclaration || resource.Type != SkillType.Active || resource.IsFeatureSkill ||
                !resource.Path.StartsWith("skill/demonicswordman/", StringComparison.OrdinalIgnoreCase) ||
                resource.Path.Contains("/notapplicable/", StringComparison.OrdinalIgnoreCase)) continue;
            int slot = Enumerable.Range(150, 48).FirstOrDefault(slot => !occupied.Contains((byte)slot), -1);
            if (slot < 0) return new(SkillLearningFailure.Capacity, null, Array.Empty<LearnedSkill>());
            occupied.Remove(skill.Slot);
            occupied.Add((byte)slot);
            skills[i] = skill with { Slot = (byte)slot };
            changed = true;
        }
        if (!changed) return Success(state);
        var next = state with { Trees = Array.AsReadOnly(state.Trees.Select(value => value.Context == context
            ? value with { Skills = Array.AsReadOnly(skills.OrderBy(skill => skill.Slot).ToArray()) } : value).ToArray()) };
        return Success(next) with { RequiresSynchronization = true };
    }

    private static SkillLearningResult Success(CharacterLearningState state) => new(SkillLearningFailure.None, state, Array.Empty<LearnedSkill>());
    private static SkillLearningResult Fail() => new(SkillLearningFailure.Unsupported, null, Array.Empty<LearnedSkill>());
}
