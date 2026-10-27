using DfoServer.Game.Session;
using DfoServer.Game.Accounts;
using System.Net;
using DfoServer.GameWorld.Channels;

namespace DfoServer.Network.Protocol;

internal sealed class GameConnectionContext(ConnectionContext connection, AccountOwners.Lease owner, GameSession session, GameConnectionSender sender,
    IGameConnectionActivity? activity = null, ChannelIdentity channel = default)
{
    public DfoServer.Network.Projection.PartyDirectoryProjection? PartyDirectory { get; set; }
    // 仅记录本连接实际写出的攻坚展示，不拥有玩法状态；断线/新连接自然失效。
    internal DfoServer.Network.Projection.RaidCombatDisplayIdentity? RaidDisplay { get; set; }
    internal DfoServer.Network.Projection.LukePhaseExitProjection? LukePhaseExit { get; set; }
    public int Id => connection.Id;
    public IPAddress Peer => connection.Peer;
    public CancellationToken Stop => connection.Stop;
    public AccountOwners.Lease Owner { get; } = owner;
    public GameSession Session { get; } = session;
    public GameConnectionSender Sender { get; } = sender;
    // 后台翻牌与本连接收包共用执行边界，提交后的差量发布完才允许下次资产修改。
    public SemaphoreSlim CommandGate => Session.CommandGate;
    internal DispatchGateScope? DispatchGates { get; set; }
    public DfoServer.Network.Projection.DungeonElevatorProjection? Elevator { get; set; }
    public DfoServer.Network.Projection.DungeonAltarProjection? Altar { get; set; }
    public DfoServer.Network.Projection.LostLandProjection? LostLand { get; set; }
    public ChannelIdentity Channel => Session.ChannelEntry?.Identity ?? channel;
    public DfoServer.Network.Projection.DungeonDeathProjection? Death { get; set; }
    public DfoServer.Network.Projection.DungeonCardProjection? Cards { get; set; }
    // 大会副本宝箱每行已选卡序号（255=未选），仅在发出 NOTI388 后使用。
    public byte[] TournamentRewardSelection { get; } = [255, 255];
    public DfoServer.Network.Projection.PremiumContractProjection? Premiums { get; set; }
    public DfoServer.Network.Projection.CreatureRuntimeProjection? Creatures { get; set; }
    // 每日礼盒只保存已发视图与连接时钟，不拥有角色次数。
    public DfoServer.Network.Projection.Inventory.DailyRetainedBoosterProjection? BoosterUses { get; set; }
    public DfoServer.Network.Projection.DazzlementTowerProjection? Dazzlement { get; set; }
    // 名册请求的协议握手状态；Selected/Ready 只保存在 GameSession。
    public bool RosterRequested { get; set; }
    internal DfoServer.Network.Projection.LukeRewardResultProjection LukeResults { get; } = new();
    // 返回选角时服务端已主动推送名册；用于吞掉客户端随后重复的 C2S8，避免双重名册刷新。
    public bool SelectionRosterPushed { get; set; }
    // 可选活动只暴露操作门禁与连接收尾；具体状态由对应模块拥有。
    public IGameConnectionActivity? Activity { get; } = activity;
    public bool BlocksTownActions => Activity?.BlocksTownActions == true ||
        Session.Phase is SessionPhase.Loading or SessionPhase.Dungeon or SessionPhase.Settlement or SessionPhase.PvpRoom;

    public void Disconnect() => connection.Disconnect();

    public Task Reply(CmdPacketType opcode, byte[] body, string action, Func<bool>? canSend = null) => Sender.Send(1, (ushort)opcode, body, action, canSend);
    public Task Notify(NotiPacketType opcode, byte[] body, string action, Func<bool>? canSend = null) => Sender.Send(0, (ushort)opcode, body, action, canSend);
    public Task NotifyProtected(NotiPacketType opcode, byte[] body, string action, Func<bool>? canSend = null) => Sender.SendProtected(0, (ushort)opcode, body, action, canSend);
}
