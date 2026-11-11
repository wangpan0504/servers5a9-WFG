namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public UserMode Mode => UserStatePolicy.Mode(ChannelEntry?.Type);
    public UserState GetState() => owner.Execute(() => ReadState(Phase));
    public bool CheckState(UserStateRequirement minimum) => owner.Execute(() => UserStatePolicy.Meets(ReadState(Phase), minimum));
    public bool CheckState(UserState expected) => owner.Execute(() => UserStatePolicy.IsImplemented(expected) && ReadState(Phase) == expected);

    UserState ReadState(SessionPhase phase, bool endingDungeon = false, bool endingPvp = false)
    {
        var dungeon = endingDungeon ? null : formalDungeon;
        return UserStatePolicy.Read(phase, Selected is not null, Ready,
            CurrentDungeonSelection is not null, dungeon?.Run.Phase,
            CurrentDungeonSelection?.IsTutorial == true || dungeon?.Entry.IsTutorial == true,
            !endingPvp && (pvpTraining is not null || pvpPractice is not null), !endingPvp && pvpArena is not null,
            !endingPvp && pvpArena?.Room.Match?.Phase == DfoServer.Game.Pvp.PvpArenaMatchPhase.Fighting, Mode == UserMode.Pvp);
    }

    // 业务在原owner内完成上下文安装/提交后调用；旧连接收尾也只能清理自己的会话。
    void SetState(SessionPhase next)
    {
        if (!UserStatePolicy.CanTransition(Phase, next))
            throw new InvalidOperationException($"非法会话状态切换：{Phase} -> {next}");
        if (next == SessionPhase.Loading && Phase != SessionPhase.Loading) pendingCinematicClear = null;
        if (next != SessionPhase.MapSelect) InvalidateFiendwarKeyPreparation();
        bool endingDungeon = next is not (SessionPhase.Loading or SessionPhase.Dungeon or SessionPhase.Settlement);
        bool endingPvp = next != SessionPhase.PvpRoom;
        _ = ReadState(next, endingDungeon, endingPvp);
        if (next != SessionPhase.Town) Volatile.Write(ref pvpMatching, null);
        if (endingPvp)
        {
            pvpTraining = null;
            pvpPractice = null;
            Volatile.Write(ref pvpArena, null);
        }
        if (endingDungeon)
        {
            pendingCinematicClear = null;
            CancelHostedDungeonEligibility();
            portableCargo = null;
            formalDungeon?.Run.CancelTagEvildom();
            ReleaseCurrentRaidDungeon();
            ReleaseCurrentEvildomWarDungeon();
            formalDungeon = null;
        }
        if (Phase == SessionPhase.Town && next != SessionPhase.Town)
        {
            // 离开首次入城场景即结束登录复述窗口；快速取消选图也不能重新吸收真实卸装。
            loginWearRestatementUntil = 0;
            InvalidateImageCommunication();
            InvalidateExpertJobStore();
        }
        Phase = next;
    }
}
