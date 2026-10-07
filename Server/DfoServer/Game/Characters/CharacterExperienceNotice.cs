namespace DfoServer.Game.Characters;

// 附属状态必须由各自owner显式提供；不从角色经验、等级或金币推导，也不默认清零。
internal sealed record CharacterGrowthAuxiliaryState(uint DuelVictoryPoints, uint GrowthCapsuleExperience,
    uint ProficiencyLevel, ulong ProficiencyExperience);

internal sealed record CharacterExperienceBonus(uint ResourceId, uint Experience);

// 这些是一次事件的显示分量，不是余额。原始分支值及两个未知字段不扩展为猜测枚举。
internal sealed record CharacterExperienceDisplay(uint PartyExperience, uint FatigueBuffExperience, byte BranchValue,
    uint SeriaBlessingExperience, uint GrowthContractExperience, uint FatigueBurningExperience,
    uint InternetCafeExperience, IReadOnlyList<CharacterExperienceBonus> Bonuses, uint BuffExperience,
    uint DeductedExperienceComponent, uint FastGrowthExperience, uint Unreferenced75,
    uint SeriaWelcomeExperience, uint GuildExperience, uint PotionExperience);
