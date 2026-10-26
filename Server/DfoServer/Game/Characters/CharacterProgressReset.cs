using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Characters;

internal sealed record CharacterProgressResetPlan(Character Before, byte Level, byte GrowType, CharacterLearningState Learning);

internal sealed class CharacterProgressReset(CharacterCatalog characters, SkillLearningCatalog skills, CharacterGrowthCatalog growth)
{
    public CharacterGrowthOptions GetOptions(byte job) => characters.Get(job).GrowthOptions;

    // Level保留已学技能并按新等级重算余额；Grow仍重建双技能树。
    public CharacterProgressResetPlan Prepare(Character before, CharacterProgressEdit request) =>
        request.Field == CharacterProgressField.Level ? PrepareLevel(before, request) : PrepareReset(before, request);

    // GM改等级：保留已学技能、槽位（快捷栏）、指令与连招，只按新等级的总来源（含额外SP/TP）减已学费用派生余额。
    // 降级后已学技能超出新等级门槛或费用超过新总额时整笔拒绝，不截断技能、不把负余额钳为0；额外来源未接管的旧状态同样拒绝。
    // 升级不做技能等级门槛校验：升级不会让已学技能变得更高，若仍按绝对值比较，会把既有存档里
    // 技能高于角色等级的存量状态永远锁死，既不能降也不能升。降级仍严格校验，避免落到非法组合。
    CharacterProgressResetPlan PrepareLevel(Character before, CharacterProgressEdit request)
    {
        request.Validate();
        if (request.Field != CharacterProgressField.Level || !string.Equals(request.Name, before.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("等级调整目标不一致");
        byte level = checked((byte)request.Primary);
        bool downgrade = level < before.Level;
        var learning = before.Learning;
        // 尚未初始化技能树（新建且未进入过的角色）没有可保留的学习记录，按免费基线建树与重置等价。
        if (!learning.Initialized) return PrepareReset(before, request);
        if (!SkillPointLedger.SupportsGrowth(before.GrowType, before.Job))
            throw new InvalidDataException("角色技能状态不支持保留技能改等级；可用 grow 重置技能树");
        if (learning.BonusSp is not int bonusSp || learning.BonusTp is not int bonusTp)
            throw new InvalidDataException("旧角色额外SP/TP来源尚未接管，请先进入角色一次；不按余额猜测来源");
        int totalSp = checked(skills.TotalSp(level) + bonusSp), totalTp = checked(skills.TotalTp(level) + bonusTp);
        if (totalSp > ushort.MaxValue || totalTp > ushort.MaxValue) throw new InvalidDataException("技能点总来源超出协议范围");
        int contractLevels = skills.ActiveExpertContractLevels(learning.ExpertContractExpiresAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        CharacterLearningState target;
        try
        {
            target = SkillPointLedger.Synchronize(before.Job, before.GrowType, level,
                learning with { Experience = growth.GetLevelStartExperience(level) }, skills, contractLevels);
            var free = SkillPointLedger.FreeBaseline(before.Job, before.GrowType, level, skills, contractLevels);
            foreach (var tree in target.Trees)
            {
                foreach (var skill in tree.Skills)
                {
                    var resource = skills.Get(before.Job, skill.Id);
                    if (resource.FixedLevel is not null || skill.Level <= free.GetValueOrDefault(skill.Id)) continue;
                    int required = resource.GetOrdinaryNextRequiredCharacterLevel(skill.Level - 1);
                    // 与学习门槛一致：达人契约减五对 feature TP 技能同样生效。
                    if (downgrade && level + contractLevels < required)
                        throw new InvalidDataException($"目标等级{level}低于已学技能{skill.Id} Lv{skill.Level}的需求等级{required}；请先洗点后再降级，或用 grow 重置技能树");
                }
                int sp = SkillPointLedger.Spent(tree.Skills, free, before.GrowType, id => skills.Get(before.Job, id));
                int tp = SkillPointLedger.Spent(tree.Skills, free, before.GrowType, id => skills.Get(before.Job, id), tp: true);
                if (sp > totalSp || tp > totalTp)
                    throw new InvalidDataException($"目标等级{level}的点数不足以保留已学技能（树{tree.Context}：SP已用{sp}/总额{totalSp}，TP已用{tp}/总额{totalTp}）；请先洗点后再降级，或用 grow 重置技能树");
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or KeyNotFoundException or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("已学技能无法按新等级核算，拒绝改等级: " + ex.Message);
        }
        target.Validate();
        return new(before, level, before.GrowType, target);
    }

    CharacterProgressResetPlan PrepareReset(Character before, CharacterProgressEdit request)
    {
        request.Validate();
        if (request.Field is not (CharacterProgressField.Level or CharacterProgressField.Grow) ||
            !string.Equals(request.Name, before.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("等级或转职重置目标不一致");
        byte level = request.Field == CharacterProgressField.Level ? checked((byte)request.Primary) : before.Level;
        byte grow = request.Field == CharacterProgressField.Grow ? checked((byte)request.Primary) : before.GrowType;
        var option = GetOptions(before.Job).GrowTypes.SingleOrDefault(value => value.Value == (grow & 15));
        if (option is null || (grow >> 4) > option.Awakenings.Count || !SkillPointLedger.SupportsGrowth(grow, before.Job))
            throw new InvalidDataException("当前职业不支持所选分支或觉醒阶段");
        int contractLevels = skills.ActiveExpertContractLevels(before.Learning.ExpertContractExpiresAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var free = SkillPointLedger.FreeBaseline(before.Job, grow, level, skills, contractLevels)
            .OrderBy(pair => pair.Key).Select(pair => new CharacterSkill(pair.Key, pair.Value)).ToArray();
        var initial = SkillLearning.Initialize(before.Job, free, skills);
        if (!initial.Success) throw new InvalidDataException("所选职业技能无法重建: " + initial.Failure);
        // GM明确重置学习记录；已登记的额外来源继续保留，未知旧来源不从余额猜测。
        int bonusSp = before.Learning.BonusSp ?? 0, bonusTp = before.Learning.BonusTp ?? 0;
        int sp = checked(skills.TotalSp(level) + bonusSp), tp = checked(skills.TotalTp(level) + bonusTp);
        if (sp > ushort.MaxValue || tp > ushort.MaxValue) throw new InvalidDataException("技能点总来源超出协议范围");
        var learning = initial.State! with
        {
            Experience = request.Field == CharacterProgressField.Level ? growth.GetLevelStartExperience(level) : before.Learning.Experience,
            SelectedTree = before.Learning.SelectedTree,
            BonusSp = bonusSp,
            BonusTp = bonusTp,
            ExpertContractExpiresAt = before.Learning.ExpertContractExpiresAt,
            Trees = Array.AsReadOnly(initial.State!.Trees.Select(tree => tree with { Sp = (ushort)sp, Tp = (ushort)tp }).ToArray())
        };
        learning.Validate();
        return new(before, level, grow, learning);
    }
}
