namespace DfoServer.Game.Pvp;

// 领域身份来自专用建角账本，不从等级、转职或任务完成情况反推。
internal sealed record PvpPrivateCharacterState(int RecreateDays);
internal sealed record PvpPrivateCharacterCreation(int AccountLevelRequirement,int RecreateDays,long CreatedAtUnix)
{
    public void Validate()
    {
        if(AccountLevelRequirement is <1 or >95 || RecreateDays is <0 or >36500 || CreatedAtUnix<0)
            throw new InvalidDataException("决斗专用角色创建资格数据非法");
    }
}
