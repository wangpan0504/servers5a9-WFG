using DfoServer.Game.Session;
using DfoServer.Network;
using DfoServer.Network.Builders;

namespace DfoServer.Development;

/// <summary>
/// GM <c>notice</c> 的公告出口：把 <c>NOTI 0x06AD</c> 公告帧投给在线连接。
/// <para>
/// <c>all</c> 广播给所有已进入世界的连接（复用 <see cref="OnlineSessionDirectory.ProbeAll"/>）；
/// 角色名只投该角色当前连接，角色不在线返回 <c>false</c>，由命令回 <c>ERR</c>。
/// 标题固定 <see cref="Title"/>，正文 UTF-8；两条文本的 24/600 字节上限与字符边界截断都在
/// <see cref="ServerNoticePackets"/> 里完成。
/// </para>
/// </summary>
internal sealed class GmNoticeSender(OnlineSessionDirectory online)
{
    /// <summary>GM 公告标题，固定「系统公告」。</summary>
    public const string Title = "系统公告";

    /// <summary>发送泵与 GM 审计里使用的动作名。</summary>
    public const string Action = "gm_notice";

    /// <summary>全服广播，返回实际入队的连接数（不含未就绪/未选角连接）。</summary>
    public int Broadcast(string body, out bool truncated)
    {
        byte[] frame = ServerNoticePackets.Build(Title, body, out truncated);
        return online.ProbeAll((ushort)NotiPacketType.AAS_TIPS, frame, Action);
    }

    /// <summary>只投给指定角色当前所在连接；角色不在线（或已回到选角）返回 <c>false</c>。</summary>
    public bool Send(string characterName, string body, out bool truncated)
    {
        byte[] frame = ServerNoticePackets.Build(Title, body, out truncated);
        var target = online.FindConnectionByCharacterName(characterName);
        if (target is null || !target.Session.Ready || target.Session.Selected is null)
            return false;
        target.Probe((ushort)NotiPacketType.AAS_TIPS, frame, Action);
        return true;
    }
}
