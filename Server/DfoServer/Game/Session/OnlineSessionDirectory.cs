using DfoServer.Game.Accounts;
using DfoServer.Game.Chat;
using System.Collections.Concurrent;

namespace DfoServer.Game.Session;

internal sealed record OnlineCharacterSnapshot(string Account, ushort CharacterId, string Name, bool Ready, SessionPhase Phase, UserState State);

/// <summary>
/// 按连接号定位当前在线会话及其诊断通知投递入口。只保存本连接的发送回调和同一份 Session，
/// 不复制角色、就绪状态或层 id；GM 聊天帧注入与 GM <c>notice</c> 公告用它向指定连接/全部在线连接发帧
/// （opcode 由调用方决定：<c>0x000C</c>/<c>0x03E2</c>/<c>0x06AD</c>，见 <see cref="Network.Builders.ChatProbes"/>
/// 与 <see cref="Network.Builders.ServerNoticePackets"/>）。
/// </summary>
internal sealed record ChatProbeConnection(int ConnectionId, GameSession Session, Action<ushort, byte[], string> Probe);

// GM、在线协议与聊天共用同一会话索引，不复制角色或就绪状态。
internal sealed class OnlineSessionDirectory
{
    readonly ConcurrentDictionary<string, GameSession> sessions = new(StringComparer.Ordinal);
    // 连接号 → 本连接会话与投递入口；断开时按会话身份精确移除，旧连接不能删掉接管后的新连接。
    readonly ConcurrentDictionary<int, ChatProbeConnection> connections = new();
    public void Register(AccountOwners.Lease owner, GameSession session) => owner.Execute(() =>
    {
        sessions[owner.Account] = session;
        return true;
    });
    public GameSession? Find(string account) => sessions.TryGetValue(account, out var session) ? session : null;

    /// <summary>登记一条已认证连接；<paramref name="probe"/> 只负责把 opcode 与正文入本连接发送队列。</summary>
    public void RegisterConnection(int connectionId, GameSession session, Action<ushort, byte[], string> probe) =>
        connections[connectionId] = new(connectionId, session, probe);

    /// <summary>按连接号查找在线会话；找不到返回 <c>null</c>（GM 据此回 ERR，不抛异常）。</summary>
    public ChatProbeConnection? FindConnection(int connectionId) =>
        connections.TryGetValue(connectionId, out var connection) ? connection : null;

    /// <summary>
    /// 按角色名定位当前在线连接（GM <c>notice &lt;角色名&gt;</c> 的定向公告）。
    /// 只比较已选角会话的角色名；角色不在线返回 <c>null</c>，由调用方回 ERR。
    /// </summary>
    public ChatProbeConnection? FindConnectionByCharacterName(string name) =>
        connections.Values.FirstOrDefault(connection =>
            string.Equals(connection.Session.OnlineCharacter()?.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 向所有已进入世界（就绪且有当前角色）的连接入队一帧，返回入队连接数（GM 全服公告）。
    /// 与 <see cref="DeliverAll"/> 的区别：GM 公告不经过聊天投递领域，直接复用本目录已登记的连接投递入口；
    /// 未就绪/未选角的连接不计数，发送泵写出前仍会复核一次身份。
    /// </summary>
    public int ProbeAll(ushort opcode, byte[] body, string action)
    {
        int delivered = 0;
        foreach (var connection in connections.Values.ToArray())
        {
            if (!connection.Session.Ready || connection.Session.Selected is null)
                continue;
            connection.Probe(opcode, body, action);
            delivered++;
        }
        return delivered;
    }

    /// <summary>只在连接仍指向同一会话时移除，避免旧连接误删同号的新会话。</summary>
    public void RemoveConnection(int connectionId, GameSession session)
    {
        if (connections.TryGetValue(connectionId, out var current) && ReferenceEquals(current.Session, session))
            ((ICollection<KeyValuePair<int, ChatProbeConnection>>)connections).Remove(new(connectionId, current));
    }
    public OnlineCharacterSnapshot[] Snapshot() => sessions.Values.Select(session => session.OnlineCharacter())
        .OfType<OnlineCharacterSnapshot>().OrderBy(character => character.Account, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// 在线会话广播：先取会话快照，再逐个会话在它自己的账号锁内复核状态与 <paramref name="filter"/>，
    /// 锁外调用该会话的投递回调。返回值是已交给投递回调的会话数（已入各连接的发送队列），
    /// 不代表帧已经写到 socket。
    /// <para>
    /// <paramref name="filter"/> 在目标账号锁内执行：只能比较快照字段，不得查库、取其他账号锁或做资源解析；
    /// 这样锁内只保留必要复核，网络等待与日志都留在锁外（见并发规范）。
    /// </para>
    /// </summary>
    public int DeliverAll(Func<ChatRecipient, bool> filter, ChatNotice notice)
    {
        int delivered = 0;
        foreach (var session in sessions.Values.ToArray())
            if (session.TryDeliverChat(filter, notice))
                delivered++;
        return delivered;
    }

    // GM 强制小退：账号在线且有角色在游戏中时让其回到选角；没有在线会话或角色不匹配返回 false。
    public Task<bool> ForceReturnToSelectionAsync(string account, string? characterName = null) =>
        Find(account) is { } session ? session.ForceReturnToSelectionAsync(characterName) : Task.FromResult(false);
    public bool Disconnect(string account) => Find(account)?.Disconnect() == true;
    public bool HasReadyCharacter(ushort characterId) => sessions.Values.Any(session => session.IsReadyCharacter(characterId));
    // 比较与移除必须原子完成，旧连接的 finally 不能删掉已接管的新会话。
    public void Remove(string account, GameSession session) => ((ICollection<KeyValuePair<string, GameSession>>)sessions).Remove(new(account, session));
}
