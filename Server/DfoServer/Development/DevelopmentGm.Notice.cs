using DfoServer.Infrastructure;
using DfoServer.Network;

namespace DfoServer.Development;

internal static partial class DevelopmentGm
{
    // GM 日志里的文本上限：公告正文可能很长，不能把热点日志撑大。
    const int NoticeLogTextLimit = 64;

    /// <summary>
    /// GM <c>notice &lt;all|角色名&gt; &lt;文本&gt;</c>：用已实测可见的 <c>NOTI 0x06AD</c> 公告弹框
    /// 发全服或定向公告。标题固定「系统公告」，正文 UTF-8，标题/正文按 24/600 字节截断；
    /// <c>all</c> 投所有已进入世界的在线连接，角色名只投该角色当前连接，角色不在线返回 <c>ERR</c>。
    /// 不经过玩家聊天投递路径（GM 是管理入口），也不写库。
    /// </summary>
    static string SendNotice(DevelopmentRequest request, GmNoticeSender? sender, ServerLog? log)
    {
        if (sender is null)
            throw new InvalidOperationException("公告仅支持在线服务");
        bool all = request.Name.Length == 0;
        int recipients;
        bool truncated;
        if (all)
            recipients = sender.Broadcast(request.Text, out truncated);
        else if (sender.Send(request.Name, request.Text, out truncated))
            recipients = 1;
        else
            throw new InvalidDataException($"角色 {request.Name} 不在线或不存在");
        log?.Write("gm_notice", new
        {
            Target = all ? "all" : request.Name,
            Recipients = recipients,
            Text = request.Text.Length <= NoticeLogTextLimit ? request.Text : request.Text[..NoticeLogTextLimit] + "...",
            Truncated = truncated
        });
        return $"OK notice {(all ? "all" : request.Name)} recipients={recipients} opcode=0x{(ushort)NotiPacketType.AAS_TIPS:X4}" +
            (truncated ? " (truncated)" : "");
    }
}
