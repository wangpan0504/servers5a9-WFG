using DfoServer.Game.Accounts;

namespace DfoServer.Game.Guild;

/// <summary>只记录服务端实际观察到的在线时刻，不把未观测的旧数据回填成历史事实。</summary>
internal interface IGuildPresenceStore
{
    void ObserveGuildMemberOnline(AccountOwners.Lease owner, ushort characterId, long observedAtUnix);
}

internal sealed record GuildPresenceFailure(ushort CharacterId, long ObservedAtUnix, Exception Error);
