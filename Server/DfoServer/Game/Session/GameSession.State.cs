namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public UserState GetState() => owner.Execute(() => ReadState(Phase));
    public bool CheckState(UserStateRequirement minimum) => owner.Execute(() => UserStatePolicy.Meets(ReadState(Phase), minimum));
    public bool CheckState(UserState expected) => owner.Execute(() => UserStatePolicy.IsImplemented(expected) && ReadState(Phase) == expected);

    UserState ReadState(SessionPhase phase, bool endingDungeon = false)
    {
        var dungeon = endingDungeon ? null : formalDungeon;
        return UserStatePolicy.Read(phase, Selected is not null, Ready,
            CurrentDungeonSelection is not null, dungeon?.Run.Phase,
            CurrentDungeonSelection?.IsTutorial == true || dungeon?.Entry.IsTutorial == true);
    }

    // 业务在原owner内完成上下文安装/提交后调用；旧连接收尾也只能清理自己的会话。
    void SetState(SessionPhase next)
    {
        if (!UserStatePolicy.CanTransition(Phase, next))
            throw new InvalidOperationException($"非法会话状态切换：{Phase} -> {next}");
        bool endingDungeon = next is not (SessionPhase.Loading or SessionPhase.Dungeon or SessionPhase.Settlement);
        _ = ReadState(next, endingDungeon);
        if (endingDungeon) formalDungeon = null;
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
