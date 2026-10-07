namespace DfoServer.Game.Characters;

// 新角色明确初始化；存量无记录表示未知，不能在选角时清零或补造。
internal sealed record CharacterDungeonState(uint NextObjectKey, long LastAttempt,
    uint GrowthCapsuleExperience, uint ProficiencyLevel, ulong ProficiencyExperience)
{
    public static CharacterDungeonState NewOrdinary => new(256, 0, 0, 0, 0);
    public void Validate()
    {
        if (NextObjectKey is < 1 or > 65536 || LastAttempt < 0 || ProficiencyExperience > long.MaxValue)
            throw new InvalidDataException("无效角色副本owner状态");
    }
}
