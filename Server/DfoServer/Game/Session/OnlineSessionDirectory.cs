using DfoServer.Game.Accounts;
using System.Collections.Concurrent;

namespace DfoServer.Game.Session;

internal sealed record OnlineCharacterSnapshot(string Account, ushort CharacterId, string Name, bool Ready, SessionPhase Phase, UserState State);

// GM 和在线协议共享同一会话索引，不复制角色或就绪状态。
internal sealed class OnlineSessionDirectory
{
    readonly ConcurrentDictionary<string, GameSession> sessions = new(StringComparer.Ordinal);
    public void Register(AccountOwners.Lease owner, GameSession session) => owner.Execute(() =>
    {
        sessions[owner.Account] = session;
        return true;
    });
    public GameSession? Find(string account) => sessions.TryGetValue(account, out var session) ? session : null;
    public OnlineCharacterSnapshot[] Snapshot() => sessions.Values.Select(session => session.OnlineCharacter())
        .OfType<OnlineCharacterSnapshot>().OrderBy(character => character.Account, StringComparer.Ordinal).ToArray();
    public bool HasReadyCharacter(ushort characterId) => sessions.Values.Any(session => session.IsReadyCharacter(characterId));
    // 比较与移除必须原子完成，旧连接的 finally 不能删掉已接管的新会话。
    public void Remove(string account, GameSession session) => ((ICollection<KeyValuePair<string, GameSession>>)sessions).Remove(new(account, session));
}
