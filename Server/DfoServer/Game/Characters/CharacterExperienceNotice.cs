using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Characters;

// 满级角色没有下一等级边界：客户端收到NOTI37后仍会在头顶显示绿色经验文本，且本地旧经验不再随升级推进。
// 满级后停发该通知；经验、熟练度与冒险团进度仍按提交结果照常入账。
internal static class CharacterExperienceNotice
{
    // 传入事件提交前的真实等级：跨入满级的那一次升级仍按正常通知发送。
    public static bool ShouldPublish(int previousLevel) =>
        previousLevel < CharacterGrowthCatalog.MaximumSupportedLevel;
}

// 附属状态必须由各自owner显式提供；不从角色经验、等级或金币推导，也不默认清零。
internal sealed record CharacterGrowthAuxiliaryState(uint DuelVictoryPoints, uint GrowthCapsuleExperience,
    uint ProficiencyLevel, ulong ProficiencyExperience);

internal sealed record CharacterExperienceBonus(uint ResourceId, uint Experience);

// 这些是一次事件的显示分量，不是余额。原始分支值及两个未知字段不扩展为猜测枚举。
internal sealed record CharacterExperienceDisplay(uint PartyExperience, uint FatigueBuffExperience, byte BranchValue,
    uint SeriaBlessingExperience, uint GrowthContractExperience, uint FatigueBurningExperience,
    uint InternetCafeExperience, IReadOnlyList<CharacterExperienceBonus> Bonuses, uint BuffExperience,
    uint DeductedExperienceComponent, uint FastGrowthExperience, uint Unreferenced75,
    uint SeriaWelcomeExperience, uint GuildExperience, uint PotionExperience)
{
    // 普通单人基础经验由客户端使用累计XP差计算；这里没有队伍或契约等额外贡献。
    public static CharacterExperienceDisplay Ordinary { get; } = new(0, 0, 0, 0, 0, 0, 0,
        Array.Empty<CharacterExperienceBonus>(), 0, 0, 0, 0, 0, 0, 0);
}
