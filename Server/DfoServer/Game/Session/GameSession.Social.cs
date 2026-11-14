using DfoServer.Game.Social;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public Action<SocialView>? SocialNoticeArrived { get; set; }
    public Action<string, Func<bool>>? SocialAccountDeleted { get; set; }

    // 在源账号锁内固定身份；接收者发送泵只读失效标记，不能再取源账号锁。
    internal Func<bool> CaptureSocialPresenceGuard()
    {
        try
        {
            return owner.Execute(() =>
            {
                long epoch = SelectionEpoch;
                bool ready = Ready;
                ushort? character = Selected?.Id;
                return new Func<bool>(() => owner.IsCurrent && SelectionEpoch == epoch &&
                    Ready == ready && Selected?.Id == character);
            });
        }
        // 目录尚未清理的旧会话已离线；调用方仍须复核目录未被新会话替换。
        catch (InvalidOperationException) when (!owner.IsCurrent) { return () => !owner.IsCurrent; }
    }

    public SocialActor? CaptureSocialActor()
    {
        try
        {
            return owner.Execute(() => Ready && Selected is { } character
                ? new SocialActor(owner, this, character, SelectionEpoch) : null);
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return null; }
    }
}
