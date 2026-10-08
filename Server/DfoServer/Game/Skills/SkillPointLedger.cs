using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Skills;

// 总来源减已学技能成本；余额是存档和协议镜像，额外SP独立保留。
internal static class SkillPointLedger
{
    public static bool SupportsGrowth(byte growth, byte job = 0) => (growth & 15) <= 5 && (growth >> 4) <= 2 &&
        ((growth & 15) != 0 || (growth >> 4) == 0 || job is 9 or 10);

    public static Dictionary<ushort, byte> FreeBaseline(byte job, byte growth, byte level,
        SkillLearningCatalog catalog, int contractLevels = 0)
    {
        var free = catalog.FreeSkills(job, growth).ToDictionary(skill => skill.Id, skill => skill.Level);
        foreach (var skill in catalog.FixedSkills(job))
        {
            int rank = skill.GetFixedLevel(level, growth, contractLevels);
            if (rank > 0) free[skill.SkillId] = checked((byte)Math.Max(rank, free.GetValueOrDefault(skill.SkillId)));
        }
        return free;
    }

    public static int Spent(IEnumerable<LearnedSkill> skills, IReadOnlyDictionary<ushort, byte> free,
        byte growth, Func<ushort, SkillLearningDefinition> definition, bool tp = false)
    {
        int used = 0;
        foreach (var skill in skills)
        {
            int baseline = free.GetValueOrDefault(skill.Id);
            if (skill.Level <= baseline) continue;
            var resource = definition(skill.Id);
            if (resource.IsFeatureSkill != tp) continue;
            if (!resource.SupportsPointLearning || resource.FixedLevel is not null)
                throw new NotSupportedException("已学技能费用不在普通SP范围");
            for (int rank = baseline; rank < skill.Level; rank++)
                used = checked(used + resource.GetPointCost(rank, growth));
        }
        return used;
    }

    public static CharacterLearningState Synchronize(byte job, byte growth, byte level, CharacterLearningState state,
        SkillLearningCatalog catalog, int contractLevels = 0)
    {
        if (!state.Initialized || !SupportsGrowth(growth, job)) return state;
        var free = FreeBaseline(job, growth, level, catalog, contractLevels);
        if (!state.BonusSp.HasValue)
        {
            // 仅接管两树都能完整核算、且总来源相同的旧状态；负差或不一致保持原存档。
            int[] totals;
            try
            {
                totals = state.Trees.Select(tree => checked(tree.Sp + Spent(tree.Skills, free, growth, id => catalog.Get(job, id)))).ToArray();
            }
            catch (NotSupportedException) { return state; }
            catch (KeyNotFoundException) { return state; }
            int bonus = totals[0] - catalog.TotalSp(level);
            if (totals[0] != totals[1] || totals[0] > ushort.MaxValue || bonus < 0 || bonus > ushort.MaxValue) return state;
            state = state with { BonusSp = bonus };
        }
        if (!state.BonusTp.HasValue)
        {
            int[] totals;
            try { totals = state.Trees.Select(tree => checked(tree.Tp + Spent(tree.Skills, free, growth, id => catalog.Get(job, id), tp: true))).ToArray(); }
            catch (NotSupportedException) { return state; }
            int bonus = totals[0] - catalog.TotalTp(level);
            if (totals[0] == totals[1] && bonus is >= 0 and <= 65535) state = state with { BonusTp = bonus };
        }
        var trees = new List<CharacterSkillTree>();
        bool changed = false;
        foreach (var tree in state.Trees)
        {
            var skills = tree.Skills.ToDictionary(skill => skill.Id);
            foreach (var grant in free)
            {
                if (skills.TryGetValue(grant.Key, out var current) && current.Level >= grant.Value) continue;
                var resource = catalog.Get(job, grant.Key);
                int slot = current?.Slot ?? SkillSlotAllocator.Allocate(resource, skills.Values.Select(skill => skill.Slot).ToHashSet());
                if (slot < 0) throw new NotSupportedException("免费技能没有可用分组槽位: " + resource.Path);
                skills[grant.Key] = current is null ? new((byte)slot, grant.Key, grant.Value) : current with { Level = grant.Value };
                changed = true;
            }
            foreach (var skill in skills.Values.ToArray())
            {
                var resource = catalog.Get(job, skill.Id);
                int target = skill.Level;
                if (resource.FixedLevel is not null)
                    target = Math.Max(resource.GetFixedLevel(level, growth, contractLevels),
                        catalog.FreeSkills(job, growth).FirstOrDefault(value => value.Id == skill.Id)?.Level ?? 0);
                else if (state.ExpertContractExpiresAt > 0 && contractLevels == 0 && resource.SupportsOrdinarySpLearning)
                {
                    int cap = Math.Min(resource.GetMaximum(growth), resource.DeclaredMaximumLevels![growth & 15]);
                    while (cap > 0 && resource.GetOrdinaryNextRequiredCharacterLevel(cap - 1) > level) cap--;
                    target = Math.Min(target, Math.Max(cap, free.GetValueOrDefault(skill.Id)));
                }
                if (target == skill.Level) continue;
                if (target == 0) skills.Remove(skill.Id);
                else skills[skill.Id] = skill with { Level = checked((byte)target) };
                changed = true;
            }
            int total = checked(catalog.TotalSp(level) + state.BonusSp.Value);
            int used = Spent(skills.Values, free, growth, id => catalog.Get(job, id));
            ushort sp = checked((ushort)Math.Max(0, total - used));
            ushort tp = state.BonusTp is int tpBonus ? checked((ushort)Math.Max(0, catalog.TotalTp(level) + tpBonus - Spent(skills.Values, free, growth, id => catalog.Get(job, id), tp: true))) : tree.Tp;
            changed |= sp != tree.Sp || tp != tree.Tp;
            var combos = tree.Combos.Select(combo => combo with { Children = Array.AsReadOnly(combo.Children.Where(skills.ContainsKey).ToArray()) }).ToArray();
            var next = tree with { Sp = sp, Tp = tp, Skills = Array.AsReadOnly(skills.Values.OrderBy(skill => skill.Slot).ToArray()), Combos = Array.AsReadOnly(combos) };
            changed |= !next.SameCombos(tree);
            trees.Add(next);
        }
        return changed ? state with { Trees = trees.AsReadOnly() } : state;
    }

    public static SkillLearningResult Reset(byte job, byte growth, byte level, CharacterLearningState state,
        byte context, SkillLearningCatalog catalog, long now)
    {
        if (context > 1 || !SupportsGrowth(growth, job) || !state.Initialized)
            return new(SkillLearningFailure.Unsupported, null, Array.Empty<LearnedSkill>());
        int contractLevels = state.ExpertContractExpiresAt > now ? 5 : 0;
        var synchronized = Synchronize(job, growth, level, state, catalog, contractLevels);
        if (synchronized.BonusSp is not int bonus || synchronized.BonusTp is not int tpBonus)
            return new(SkillLearningFailure.Unsupported, null, Array.Empty<LearnedSkill>());
        var free = FreeBaseline(job, growth, level, catalog, contractLevels);
        var trees = synchronized.Trees.Select(tree => tree.Context != context ? tree : tree with
        {
            Sp = checked((ushort)(catalog.TotalSp(level) + bonus)),
            Tp = checked((ushort)(catalog.TotalTp(level) + tpBonus)),
            Combos = Array.AsReadOnly(tree.Combos.Select(combo => combo with { Children = Array.AsReadOnly(combo.Children.Where(free.ContainsKey).ToArray()) }).ToArray()),
            Skills = Array.AsReadOnly(tree.Skills.Where(skill => free.ContainsKey(skill.Id))
                .Select(skill => skill with { Level = free[skill.Id] }).OrderBy(skill => skill.Slot).ToArray())
        }).ToArray();
        bool same = ReferenceEquals(synchronized, state) && trees.Zip(state.Trees).All(pair =>
            pair.First.Context == pair.Second.Context && pair.First.Sp == pair.Second.Sp && pair.First.Tp == pair.Second.Tp &&
            pair.First.Skills.SequenceEqual(pair.Second.Skills) && pair.First.SameCombos(pair.Second));
        return new(SkillLearningFailure.None, same ? state : synchronized with { Trees = Array.AsReadOnly(trees) }, Array.Empty<LearnedSkill>())
        { TotalSp = checked(catalog.TotalSp(level) + bonus), TotalTp = checked(catalog.TotalTp(level) + tpBonus) };
    }
}
