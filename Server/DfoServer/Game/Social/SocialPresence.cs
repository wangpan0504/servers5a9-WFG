using DfoServer.Game.Accounts;

namespace DfoServer.Game.Social;

/// <summary>只记录服务端实际观察到的账号在线时刻，不把未知历史伪造为已知离线时间。</summary>
internal interface ISocialPresenceStore
{
    void ObserveSocialAccountOnline(AccountOwners.Lease owner, long observedAtUnix);
}

internal sealed record SocialPresenceFailure(string Account, long ObservedAtUnix, Exception Error);
