namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public UserState GetState() => owner.Execute(() => ReadState(Phase));
    public bool CheckState(UserStateRequirement minimum) => owner.Execute(() => UserStatePolicy.Meets(ReadState(Phase), minimum));
    public bool CheckState(UserState expected) => owner.Execute(() => UserStatePolicy.IsImplemented(expected) && ReadState(Phase) == expected);

    UserState ReadState(SessionPhase phase) => UserStatePolicy.Read(phase, Selected is not null, Ready,
        CurrentDungeonSelection is not null, formalDungeon?.Run.Phase,
        CurrentDungeonSelection?.IsTutorial == true || formalDungeon?.Entry.IsTutorial == true);

    // 业务在原owner内完成上下文安装/提交后调用；旧连接收尾也只能清理自己的会话。
    void SetState(SessionPhase next)
    {
        if (!UserStatePolicy.CanTransition(Phase, next))
            throw new InvalidOperationException($"非法会话状态切换：{Phase} -> {next}");
        _ = ReadState(next);
        if (Phase == SessionPhase.Town && next != SessionPhase.Town)
        {
            InvalidateImageCommunication();
            InvalidateExpertJobStore();
        }
        Phase = next;
    }
}
