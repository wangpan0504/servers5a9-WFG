using DfoServer.Game.Session;
using DfoServer.Game.Accounts;
using System.Net;

namespace DfoServer.Network.Protocol;

internal sealed class GameConnectionContext(ConnectionContext connection, AccountOwners.Lease owner, GameSession session, GameConnectionSender sender,
    IGameConnectionActivity? activity = null)
{
    public int Id => connection.Id;
    public IPAddress Peer => connection.Peer;
    public CancellationToken Stop => connection.Stop;
    public AccountOwners.Lease Owner { get; } = owner;
    public GameSession Session { get; } = session;
    public GameConnectionSender Sender { get; } = sender;
    public DfoServer.Network.Projection.DungeonElevatorProjection? Elevator { get; set; }
    public DfoServer.Network.Projection.DungeonCardProjection? Cards { get; set; }
    // 名册请求的协议握手状态；Selected/Ready 只保存在 GameSession。
    public bool RosterRequested { get; set; }
    // 返回选角时服务端已主动推送名册；用于吞掉客户端随后重复的 C2S8，避免双重名册刷新。
    public bool SelectionRosterPushed { get; set; }
    // 可选活动只暴露操作门禁与连接收尾；具体状态由对应模块拥有。
    public IGameConnectionActivity? Activity { get; } = activity;
    public bool BlocksTownActions => Activity?.BlocksTownActions == true ||
        Session.Phase is SessionPhase.Loading or SessionPhase.Dungeon or SessionPhase.Settlement;

    public Task Reply(CmdPacketType opcode, byte[] body, string action, Func<bool>? canSend = null) => Sender.Send(1, (ushort)opcode, body, action, canSend);
    public Task Notify(NotiPacketType opcode, byte[] body, string action, Func<bool>? canSend = null) => Sender.Send(0, (ushort)opcode, body, action, canSend);
}
