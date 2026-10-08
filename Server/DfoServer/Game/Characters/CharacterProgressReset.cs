using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Characters;

internal sealed record CharacterProgressResetPlan(Character Before, byte Level, byte GrowType, CharacterLearningState Learning);

internal sealed class CharacterProgressReset(CharacterCatalog characters, SkillLearningCatalog skills, CharacterGrowthCatalog growth)
{
    public CharacterGrowthOptions GetOptions(byte job) => characters.Get(job).GrowthOptions;

    public CharacterProgressResetPlan Prepare(Character before, CharacterProgressEdit request)
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
        int contractLevels = before.Learning.ExpertContractExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? 5 : 0;
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
            BonusSp = bonusSp,
            BonusTp = bonusTp,
            ExpertContractExpiresAt = before.Learning.ExpertContractExpiresAt,
            Trees = Array.AsReadOnly(initial.State!.Trees.Select(tree => tree with { Sp = (ushort)sp, Tp = (ushort)tp }).ToArray())
        };
        learning.Validate();
        return new(before, level, grow, learning);
    }
}
