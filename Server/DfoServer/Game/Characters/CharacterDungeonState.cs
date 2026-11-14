namespace DfoServer.Game.Characters;

// 新角色明确初始化；存量无记录表示未知，不能在选角时清零或补造。
internal sealed record CharacterDungeonState(uint NextObjectKey, long LastAttempt,
    uint GrowthCapsuleExperience, uint ProficiencyLevel, ulong ProficiencyExperience)
{
    public const uint FirstObjectKey = 256;
    public int PracticeDay { get; init; }
    public byte PracticeUsed { get; init; }
    public static CharacterDungeonState NewOrdinary => new(FirstObjectKey, 0, 0, 0, 0);
    public void Validate()
    {
        if (NextObjectKey is < 1 or > 65536 || LastAttempt < 0 || ProficiencyExperience > long.MaxValue ||
            PracticeDay is < 0 or > 3652058 || PracticeUsed > 3 || PracticeDay == 0 && PracticeUsed != 0)
            throw new InvalidDataException("无效角色副本owner状态");
    }
}
