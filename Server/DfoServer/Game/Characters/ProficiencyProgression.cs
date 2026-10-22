using DfoServer.Game.Adventure;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Characters;

// 熟练度是角色级进度：等级 0 表示挑战者初始态，经验是当前级内进度，不是累计值。
internal readonly record struct ProficiencyProgress(uint Level, ulong Experience);

internal sealed record ProficiencyAdvance(ProficiencyProgress Before, ProficiencyProgress After);

internal static class ProficiencyProgression
{
    // 与冒险团满级溢出使用同一条判定：满级后整笔经验计入；跨过 95 级入口线的一单只计入口线以上部分。
    public static ulong Gain(int previousLevel, ulong previousExperience, uint gained, CharacterGrowthCatalog catalog) =>
        AdventureProgression.MaximumLevelExperience(previousLevel, previousExperience, gained,
            CharacterGrowthCatalog.MaximumSupportedLevel,
            catalog.GetLevelStartExperience(CharacterGrowthCatalog.MaximumSupportedLevel));

    // 单笔经验可以连升多级；289 级封顶后余量丢弃，与客户端只暴露当前级进度一致。
    public static ProficiencyAdvance? Advance(ProficiencyProgress before, ulong gain, CharacterGrowthCatalog catalog)
    {
        var table = catalog.Proficiency;
        if (before.Level > ProficiencyCatalog.MaximumLevel ||
            before.Level == ProficiencyCatalog.MaximumLevel && before.Experience != 0 ||
            before.Level < ProficiencyCatalog.MaximumLevel && before.Experience >= table.Requirement(before.Level))
            throw new InvalidDataException("熟练度持久状态非法");
        if (gain == 0) return null;
        uint level = before.Level;
        ulong experience = before.Experience;
        while (gain > 0 && level < ProficiencyCatalog.MaximumLevel)
        {
            ulong remaining = table.Requirement(level) - experience;
            if (gain < remaining)
            {
                experience += gain;
                break;
            }
            gain -= remaining;
            level++;
            experience = 0;
        }
        // 满级后余量丢弃；没有实际变化时不产生写入计划。
        return level == before.Level && experience == before.Experience ? null : new(before, new(level, experience));
    }
}
