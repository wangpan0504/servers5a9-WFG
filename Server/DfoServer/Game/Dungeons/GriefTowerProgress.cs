namespace DfoServer.Game.Dungeons;

// 次数与楼层归持久账本；这里只保存本次只读快照，不另建在线角色状态。
internal sealed record GriefTowerProgress(GriefTowerEntryState State, int AdventureLevel,
    Guid InstanceId, long TaskAttempt);

internal sealed record GriefTowerAdmission(GriefTowerProgress Before, GriefTowerEntryDecision Decision,
    int DungeonId)
{
    public GriefTowerEntryState After
    {
        get
        {
            var state = Before.State;
            var decision = Decision;
            if (!decision.Accepted || decision.Day <= 0 || decision.Day < state.AccountDay ||
                decision.Day < state.CharacterDay || decision.AccountLimit <= 0 ||
                decision.UsesInvitation != (state.CharacterDay == decision.Day) ||
                state.CharacterDay == decision.Day && state.CharacterCleared)
                throw new InvalidDataException("悲叹准入规划与原日状态不一致");
            int used = state.AccountDay == decision.Day ? state.AccountUsed : 0;
            if (!decision.UsesInvitation && used >= decision.AccountLimit)
                throw new InvalidDataException("悲叹账号额度已耗尽");
            return new(decision.Day, checked(used + (decision.UsesInvitation ? 0 : 1)),
                decision.Day, false, state.Layer);
        }
    }
}
