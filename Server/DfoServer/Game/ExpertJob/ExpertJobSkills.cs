using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.ExpertJob;

internal static class ExpertJobSkills
{
    public static CharacterLearningState Grant(byte job, CharacterLearningState state,
        IReadOnlyList<ExpertJobSkill> grants, SkillLearningCatalog skills)
    {
        state.Validate();
        if (!state.Initialized) throw new NotSupportedException("副职业赠技需要已初始化的双树");
        return Replace(state, state.Trees.Select(tree => GrantTree(job, tree, grants, skills)).ToArray());
    }

    public static CharacterLearningState Preserve(byte job, CharacterLearningState initialized,
        CharacterLearningState previous, SkillLearningCatalog skills) => Replace(initialized,
            initialized.Trees.Select(tree => GrantTree(job, tree, previous.Trees.Single(old => old.Context == tree.Context).Skills
                .Where(skill => skills.Get(job, skill.Id).IsExpertSkill)
                .Select(skill => new ExpertJobSkill(skill.Id, skill.Level)).ToArray(), skills)).ToArray());

    public static CharacterLearningState Remove(CharacterLearningState state, IReadOnlyList<ExpertJobSkill> grants)
    {
        var ids = grants.Select(skill => skill.Id).ToHashSet();
        return Replace(state, state.Trees.Select(tree => !tree.Skills.Any(skill => ids.Contains(skill.Id)) ? tree : tree with
        {
            Skills = Array.AsReadOnly(tree.Skills.Where(skill => !ids.Contains(skill.Id)).ToArray())
        }).ToArray());
    }

    static CharacterSkillTree GrantTree(byte job, CharacterSkillTree tree, IReadOnlyList<ExpertJobSkill> grants,
        SkillLearningCatalog catalog)
    {
        var entries = tree.Skills.ToDictionary(skill => skill.Id);
        var occupied = tree.Skills.Select(skill => skill.Slot).ToHashSet();
        bool changed = false;
        foreach (var grant in grants)
        {
            var resource = catalog.Get(job, grant.Id);
            if (!resource.IsExpertSkill || resource.Type is not (SkillType.Active or SkillType.Passive) || grant.Level == 0)
                throw new NotSupportedException("副职业赠技缺少匹配的 expert skill 声明: " + resource.Path);
            if (entries.TryGetValue(grant.Id, out var old) && old.Level >= grant.Level) continue;
            int slot = old?.Slot ?? SkillSlotAllocator.Allocate(resource, occupied);
            if (slot < 0) throw new NotSupportedException("副职业赠技没有可用技能槽: " + resource.Path);
            entries[grant.Id] = old is null ? new((byte)slot, grant.Id, grant.Level) : old with { Level = grant.Level };
            occupied.Add((byte)slot);
            changed = true;
        }
        return changed ? tree with { Skills = Array.AsReadOnly(entries.Values.OrderBy(skill => skill.Slot).ToArray()) } : tree;
    }

    static CharacterLearningState Replace(CharacterLearningState state, CharacterSkillTree[] trees)
    {
        if (trees.Zip(state.Trees).All(pair => ReferenceEquals(pair.First, pair.Second))) return state;
        var result = state with { Trees = Array.AsReadOnly(trees) };
        result.Validate();
        return result;
    }
}
