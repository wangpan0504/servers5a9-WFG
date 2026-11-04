using DfoServer.Game.Session;
using DfoServer.Game.Accounts;
using System.Net;
using DfoServer.GameWorld.Channels;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Protocol;

internal sealed record PartyCardTemplate(DfoServer.Game.Dungeons.PartyDungeonInstance Instance,
    DfoServer.Game.Dungeons.FormalDungeonReceipt Receipt, byte[] Body);

internal sealed class GameConnectionContext(ConnectionContext connection, AccountOwners.Lease owner, GameSession session, GameConnectionSender sender,
    IGameConnectionActivity? activity = null, ChannelIdentity channel = default)
{
    // 仅记录本连接已写出的任务期段；账号任务状态仍由原Session和仓储持有。
    internal (long Epoch, long Period)? BattlePassMissionDisplay { get; set; }
    // 本连接当前目录的账号材料显示槽；余额仍只取Selected的钱包，不进入实体库存。
    internal (long Epoch, int[] ItemIds)? ActivityCalendarItems { get; set; }
    internal PartyCardTemplate? PartyCardTemplate { get; set; }
    internal DfoServer.Network.Projection.FiendwarCounterRoom? FiendwarCounterRoom { get; set; }
    internal Func<Task>? AfterDispatch { get; set; }
    internal DfoServer.Network.Projection.ServerEventProjection? ServerEvents { get; set; }
    internal DfoServer.Network.Projection.DailyAttendanceProjection? DailyAttendance { get; set; }
    internal DfoServer.Network.Projection.Strongway2019Projection? Strongway2019 { get; set; }
    public DfoServer.Network.Projection.PartyDirectoryProjection? PartyDirectory { get; set; }
    // 仅记录本连接实际写出的攻坚展示，不拥有玩法状态；断线/新连接自然失效。
    internal DfoServer.Network.Projection.RaidCombatDisplayIdentity? RaidDisplay { get; set; }
    internal byte[]? RaidRosterDisplay { get; set; }
    internal DfoServer.Game.Raids.RaidWeeklyCycle? RaidClearLukeCycle { get; init; }
    internal (long Epoch, byte[] Body)? RaidClearDisplay { get; set; }
    internal byte[]? RaidParticipationBody { get; set; }
    internal DfoServer.Game.Raids.RaidRunDisplay[] RaidParticipationRows { get; set; } = [];
    internal byte[]? RaidHpBody { get; set; }
    internal (Guid Run, bool Available)? RaidRetryDisplay { get; set; }
    internal (Guid Instance, byte[] Body)? LukeCardsDisplay { get; set; }
    internal (long Epoch, int Day)? LukeChannelCardsDisplay { get; set; }
    internal Guid LukeNamedInstance { get; set; }
    internal Dictionary<int, bool> LukeNamedDisplays { get; } = [];
    internal DfoServer.Network.Projection.RaidCombatDisplayIdentity? RaidMoviePrompt { get; set; }
    internal DfoServer.Network.Projection.RaidCombatDisplayIdentity? RaidMovieCards { get; set; }
    internal DfoServer.Network.Projection.RaidCombatDisplayIdentity? AntonCardsDisplay { get; set; }
    internal HashSet<DfoServer.GameWorld.Raids.RaidTimer> AntonTimerDisplays { get; } = [];
    // 最多保留当前团本的节点展示；只在写帧成功后记录，避免重复Clear触发原生横幅。
    internal DfoServer.Network.Projection.AntonSymbolDisplay AntonSymbols { get; } = new();
    internal Guid AntonDungeonDisplayInstance { get; set; }
    // 符号2的重复1会重新播火山字幕；仅记录本人连接实际写出的值。
    internal (Guid Instance, int Value)? AntonMeteoDisplay { get; set; }
    // 当前连接、当前实例最多6个影片符号；发送FIFO确认后更新。
    internal Guid AntonMovieDisplayInstance { get; set; }
    internal Dictionary<int, long> AntonMovieDisplays { get; } = [];
    internal System.Collections.Immutable.ImmutableDictionary<ushort, int> FiendwarUsers { get; set; } = System.Collections.Immutable.ImmutableDictionary<ushort, int>.Empty;
    internal System.Collections.Immutable.ImmutableDictionary<int, DfoServer.Game.Raids.FiendwarUnit> FiendwarEnemies { get; set; } = System.Collections.Immutable.ImmutableDictionary<int, DfoServer.Game.Raids.FiendwarUnit>.Empty;
    internal System.Collections.Immutable.ImmutableDictionary<int, DfoServer.Game.Raids.FiendwarUnit> FiendwarMonsters { get; set; } = System.Collections.Immutable.ImmutableDictionary<int, DfoServer.Game.Raids.FiendwarUnit>.Empty;
    internal System.Collections.Immutable.ImmutableDictionary<(byte Area, byte Kind), FiendwarTimerRow> FiendwarTimers { get; set; } = System.Collections.Immutable.ImmutableDictionary<(byte, byte), FiendwarTimerRow>.Empty;
    internal (Guid Instance, long Sequence)? FiendwarSupportDisplay { get; set; }
    // 仅当前实例的五个原生影片符号和实际区域；FIFO写帧成功后才确认事件代。
    internal Guid FiendwarEventDisplayInstance { get; set; }
    internal Dictionary<int, long> FiendwarMovieDisplays { get; } = [];
    internal Dictionary<int, long> FiendwarWarningDisplays { get; } = [];
    internal Dictionary<int, DfoServer.GameWorld.Raids.RaidDungeonState> AntonDungeonDisplays { get; } = [];
    internal Dictionary<DfoServer.GameWorld.Raids.RaidTimer, DfoServer.Network.Projection.LukeTimerDisplay> LukeTimerDisplays { get; } = [];
    internal DfoServer.Network.Projection.LukePhaseExitProjection? LukePhaseExit { get; set; }
    public int Id => connection.Id;
    public IPAddress Peer => connection.Peer;
    public CancellationToken Stop => connection.Stop;
    public AccountOwners.Lease Owner { get; } = owner;
    public GameSession Session { get; } = session;
    public DfoServer.Network.Projection.ChatEmoticonProjection? ChatEmoticons { get; set; }
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
    public DfoServer.Network.Projection.PremiumContractProjection? Premiums { get; set; }
    public DfoServer.Network.Projection.PcRoomProjection? PcRoom { get; set; }
    public DfoServer.Network.Projection.CreatureRuntimeProjection? Creatures { get; set; }
    // 每日礼盒只保存已发视图与连接时钟，不拥有角色次数。
    public DfoServer.Network.Projection.Inventory.DailyRetainedBoosterProjection? BoosterUses { get; set; }
    public DfoServer.Network.Projection.DazzlementTowerProjection? Dazzlement { get; set; }
    public DfoServer.Network.Projection.PcRoomTimePointProjection? PcRoomTimePoint { get; set; }
    // 名册请求的协议握手状态；Selected/Ready 只保存在 GameSession。
    public bool RosterRequested { get; set; }
    // 只记录本连接的原生握手；不改变监听端点或伪造当前频道身份。
    public bool HasEnteredWorld { get; set; }
    public bool ChannelSelectionRequired { get; set; }
    internal DfoServer.Network.Projection.LukeRewardResultProjection LukeResults { get; } = new();
    // 返回选角时服务端已主动推送名册；用于吞掉客户端随后重复的 C2S8，避免双重名册刷新。
    public bool SelectionRosterPushed { get; set; }
    // 可选活动只暴露操作门禁与连接收尾；具体状态由对应模块拥有。
    public IGameConnectionActivity? Activity { get; } = activity;
    public bool BlocksTownActions => Activity?.BlocksTownActions == true || Session.Strongway2019BlocksTownActions ||
        Session.Phase is SessionPhase.Loading or SessionPhase.Dungeon or SessionPhase.Settlement or SessionPhase.PvpRoom;

    // 私有包序号只在本次连接内递增；从1开始，回绕到0前必须结束连接，不能复用已发序号。
    uint monsterAiSerial;
    internal uint NextMonsterAiSerial()
    {
        uint next = Interlocked.Increment(ref monsterAiSerial);
        return next != 0 ? next : throw new InvalidOperationException("怪物AI私包序号已回绕，必须重建连接");
    }

    public void Disconnect() => connection.Disconnect();

    public Task Reply(CmdPacketType opcode, byte[] body, string action, Func<bool>? canSend = null) => Sender.Send(1, (ushort)opcode, body, action, canSend);
    public Task Reply(ClientFailurePacket packet, string action, Func<bool> canSend)
    {
        ArgumentNullException.ThrowIfNull(canSend);
        return Reply(packet.Opcode, packet.Body, action, canSend);
    }

    // 调用方仍提供原业务回执的发送资格；不会自动绕过事务、角色代次或场景校验。
    public Task NotifyNotice(ServerNoticeDisplay display, string text, string action, Func<bool> canSend)
    {
        ArgumentNullException.ThrowIfNull(canSend);
        return Notify(NotiPacketType.SERVER_NOTICE_MESSAGE, ServerNoticePackets.Message(display, text), action, canSend);
    }

    public Task Notify(NotiPacketType opcode, byte[] body, string action, Func<bool>? canSend = null) => Sender.Send(0, (ushort)opcode, body, action, canSend);
    public Task NotifyProtected(NotiPacketType opcode, byte[] body, string action, Func<bool>? canSend = null) => Sender.SendProtected(0, (ushort)opcode, body, action, canSend);
}
