using DfoServer.Game.Characters;

namespace DfoServer.Network.Builders;

internal static class GrowthPackets
{
    // S2C (0,37) 自身路径：固定91B及count个8B对，依据经验升级通知证据的直接读取合同。
    // 调用方负责从已提交的同一次成长事实及附属状态构建投影，本方法不发包或授予奖励。
    public static byte[] Experience(byte level, CharacterLearningState learning, CharacterGrowthAuxiliaryState auxiliary,
        CharacterExperienceDisplay display)
    {
        ArgumentNullException.ThrowIfNull(learning);
        ArgumentNullException.ThrowIfNull(auxiliary);
        ArgumentNullException.ThrowIfNull(display);
        if (level is < 1 or > 95 || learning.Trees is null ||
            learning.Trees.Any(tree => tree is null || tree.Skills is null || tree.Skills.Any(skill => skill is null)) ||
            display.Bonuses is null || display.Bonuses.Count > byte.MaxValue || display.Bonuses.Any(bonus => bonus is null))
            throw new InvalidDataException("经验通知等级、技能树或额外奖励列表非法");
        learning.Validate();
        if (!learning.Initialized)
            throw new InvalidDataException("经验通知必须显式提供完整两树余额");
        CharacterSkillTree first = learning.Trees.Single(tree => tree.Context == 0);
        CharacterSkillTree second = learning.Trees.Single(tree => tree.Context == 1);
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(level);
        writer.Write(learning.Experience);
        writer.Write(display.PartyExperience);
        writer.Write(first.Sp);
        writer.Write(second.Sp);
        writer.Write(first.Tp);
        writer.Write(second.Tp);
        writer.Write(auxiliary.DuelVictoryPoints);
        writer.Write(display.FatigueBuffExperience);
        writer.Write(display.BranchValue);
        writer.Write(display.SeriaBlessingExperience);
        writer.Write(display.GrowthContractExperience);
        // 原码以CDQ扩展该DWORD；这里保留输入位模式，不把它当角色经验增量累加。
        writer.Write(auxiliary.GrowthCapsuleExperience);
        writer.Write(display.FatigueBurningExperience);
        writer.Write(display.InternetCafeExperience);
        writer.Write(checked((byte)display.Bonuses.Count));
        foreach (CharacterExperienceBonus bonus in display.Bonuses)
        {
            writer.Write(bonus.ResourceId);
            writer.Write(bonus.Experience);
        }
        writer.Write(display.BuffExperience);
        writer.Write(display.DeductedExperienceComponent);
        writer.Write(auxiliary.ProficiencyLevel);
        writer.Write(auxiliary.ProficiencyExperience);
        writer.Write(display.FastGrowthExperience);
        writer.Write(display.Unreferenced75);
        writer.Write(display.SeriaWelcomeExperience);
        writer.Write(display.GuildExperience);
        writer.Write(display.PotionExperience);
        return bytes.ToArray();
    }
}
