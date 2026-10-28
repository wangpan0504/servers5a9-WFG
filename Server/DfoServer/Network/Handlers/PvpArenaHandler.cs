using DfoServer.Game.Pvp;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

// 50/52共用一个注册点，按真实请求和当前实例分派，训练不借正式房间状态。
internal sealed class PvpArenaHandler(PvpArenaService arena, PvpTrainingHandler training, ServerLog log, TimeProvider? clock = null) : IGameCommandModule
{
    DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    public void Register(GameCommandRegistry commands)
    {
        training.Register(commands);
        commands.Register(CmdPacketType.MAKE_PVP_ROOM, CreateAsync, nameof(PvpArenaHandler));
        commands.Register(CmdPacketType.ENTER_PVP_ROOM, EnterAsync, nameof(PvpArenaHandler),
            sharedGate: (context, packet) => arena.Find(context.Channel, PvpArenaRequests.Enter(packet.Body).Room)?.Commands);
        commands.Register(CmdPacketType.SET_PVP_SEAT_STATE, SeatAsync, nameof(PvpArenaHandler));
        commands.Register(CmdPacketType.SET_PVP_READY_STATE, ReadyAsync, nameof(PvpArenaHandler));
        commands.Register(CmdPacketType.SET_PVP_TEAM_MODE, ModeAsync, nameof(PvpArenaHandler));
        commands.Register(CmdPacketType.SET_PVP_MAP_INDEX, MapAsync, nameof(PvpArenaHandler));
        commands.Register(CmdPacketType.COMPLETE_LOAD_PVP, LoadedAsync, nameof(PvpArenaHandler));
        commands.Register(CmdPacketType.CONNECT_P2P_PVP, ConnectedAsync, nameof(PvpArenaHandler));
        commands.Register(CmdPacketType.DIE_PVP_CHARACTER, DeathAsync, nameof(PvpArenaHandler));
        commands.Register(CmdPacketType.PVP_TIME_OUT, TimeoutAsync, nameof(PvpArenaHandler));
        commands.Register(CmdPacketType.END_PVP_RESULT, ResultEndAsync, nameof(PvpArenaHandler));
        commands.Register(CmdPacketType.SET_PVP_TOTAL_MATCH_CHARAC_ORDER, TotalOrderAsync, nameof(PvpArenaHandler));
    }

    async Task<CommandResult> CreateAsync(GameConnectionContext context, Packet packet)
    {
        var request = PvpArenaRequests.Create(packet.Body);
        if (request.Kind is 1 or 3) return await training.CreateAsync(context, packet);
        if (request.Kind != 0 || request.Mode != 0) return CommandResult.Unhandled;
        var change = arena.Create(context.Session, request.Title, PvpArenaMode.Individual, request.MapIndex,
            context.BlocksTownActions, request.TitlePreset, request.Password);
        log.Write("pvp_arena_create", new { Connection = context.Id, Accepted = change is not null, Room = change?.View.Id });
        if (change is null)
            await context.Reply(CmdPacketType.MAKE_PVP_ROOM, PvpArenaPackets.Rejected(), "pvp_arena_create_rejected");
        else
        {
            try
            {
                // 新建房间门禁已由用例取得；只入队，不能持它等待慢连接。
                using var publication = context.Sender.QueuePublication(() => change.Member.IsCurrent);
                await context.Reply(CmdPacketType.MAKE_PVP_ROOM, PvpTrainingPackets.Created(),
                    "pvp_arena_created", () => change.Member.IsCurrent);
                PvpArenaProjection.Notice(context, change.Initial!);
                arena.PresenceChanged();
            }
            finally { change.Member.Room.Commands.Release(); }
        }
        return CommandResult.Handled;
    }

    async Task<CommandResult> EnterAsync(GameConnectionContext context, Packet packet)
    {
        var request = PvpArenaRequests.Enter(packet.Body);
        var room = arena.Find(context.Channel, request.Room);
        var change = room is null ? null : arena.Join(context.Session, room, false, context.BlocksTownActions, request.Password);
        log.Write("pvp_arena_enter", new { Connection = context.Id, Room = request.Room, Accepted = change is not null });
        if (change is null)
            await context.Reply(CmdPacketType.ENTER_PVP_ROOM,
                PvpArenaPackets.Rejected(room?.RequiresPassword == true && request.Password is null), "pvp_arena_enter_rejected");
        else
        {
            await context.Reply(CmdPacketType.ENTER_PVP_ROOM, PvpArenaPackets.Entered(change.View),
                "pvp_arena_entered", () => change.Member.IsCurrent);
            PvpArenaProjection.Notice(context, change.Initial!);
            arena.Publish(change.Notices);
        }
        return CommandResult.Handled;
    }

    Task<CommandResult> SeatAsync(GameConnectionContext context, Packet packet)
    {
        if (context.Session.ReadPvpTraining() is not null) return training.SeatAsync(context, packet);
        var request = PvpTrainingRequests.Seat(packet.Body);
        if (context.Session.CurrentPvpArena is not { } member || member.Room.Member(member.Token) is not { } own)
            return Task.FromResult(CommandResult.Handled);
        PvpArenaNotice[] notices;
        if (request.State == 254 && request.Seat == own.Seat)
            notices = arena.Leave(context.Session);
        else if (request.State <= 3)
            notices = arena.Move(context.Session, request.Seat, request.State == 2 ? (byte)1 : (byte)0, request.State == 3);
        else return Task.FromResult(CommandResult.Unhandled);
        arena.Publish(notices);
        log.Write("pvp_arena_seat", new { Connection = context.Id, Accepted = notices.Length != 0, request.Seat, request.State });
        return Task.FromResult(CommandResult.Handled);
    }

    Task<CommandResult> ReadyAsync(GameConnectionContext context, Packet packet)
    {
        bool ready = PvpArenaRequests.Ready(packet.Body);
        var member = context.Session.CurrentPvpArena;
        var notices = ready && member?.Room.Leader == member?.Token
            ? arena.Start(context.Session, Now) : arena.SetReady(context.Session, ready);
        arena.Publish(notices);
        log.Write("pvp_arena_ready", new { Connection = context.Id, Accepted = notices.Length != 0, Ready = ready });
        return Task.FromResult(CommandResult.Handled);
    }

    Task<CommandResult> LoadedAsync(GameConnectionContext context, Packet packet)
    {
        PvpArenaRequests.Empty(packet.Body);
        var notices = context.Session.CurrentPvpArena?.Room.Match is { } match
            ? arena.Loaded(context.Session, match.Id, Now) : [];
        arena.Publish(notices);
        log.Write("pvp_arena_loaded", new { Connection = context.Id, Accepted = notices.Length != 0 });
        return Task.FromResult(CommandResult.Handled);
    }

    Task<CommandResult> ConnectedAsync(GameConnectionContext context, Packet packet)
    {
        var seats = PvpArenaRequests.Connections(packet.Body);
        var notices = context.Session.CurrentPvpArena?.Room.Match is { } match
            ? arena.Connected(context.Session, match.Id, seats, Now) : [];
        arena.Publish(notices);
        log.Write("pvp_arena_connected", new { Connection = context.Id, Accepted = notices.Length != 0 });
        return Task.FromResult(CommandResult.Handled);
    }

    Task<CommandResult> ResultEndAsync(GameConnectionContext context, Packet packet)
    {
        PvpArenaRequests.Empty(packet.Body);
        var notices = context.Session.CurrentPvpArena?.Room.Match is { } match
            ? arena.ReturnResult(context.Session, match.Id) : [];
        arena.Publish(notices);
        log.Write("pvp_arena_result_return", new { Connection = context.Id, Accepted = notices.Length != 0 });
        return Task.FromResult(CommandResult.Handled);
    }

    Task<CommandResult> TimeoutAsync(GameConnectionContext context, Packet packet)
    {
        var values = PvpArenaRequests.Timeout(packet.Body);
        bool accepted = context.Session.CurrentPvpArena?.Room.Match is { } match &&
            arena.Timeout(context.Session, match.Id, values, Now);
        var record = accepted ? arena.RecordResult(context.Session, Now) : null;
        if (accepted && context.Session.CurrentPvpArena?.Room.IsTotal == true) arena.Publish(arena.TotalProgress(context.Session));
        log.Write("pvp_arena_timeout", new { Connection = context.Id, Accepted = accepted, RecordedMatch = record?.MatchId,
            Rule = "local-compat-lowest-team-average-v1" });
        return Task.FromResult(CommandResult.Handled);
    }

    Task<CommandResult> DeathAsync(GameConnectionContext context, Packet packet)
    {
        var report = PvpArenaRequests.Death(packet.Body);
        var notices = context.Session.CurrentPvpArena?.Room.Match is { } match
            ? arena.Death(context.Session, match.Id, report.Victim, report.Killer, Now) : [];
        var record = arena.RecordResult(context.Session, Now);
        arena.Publish(notices);
        log.Write("pvp_arena_death", new { Connection = context.Id, Accepted = notices.Length != 0,
            report.Victim, report.Killer, RecordedMatch = record?.MatchId });
        return Task.FromResult(CommandResult.Handled);
    }

    Task<CommandResult> TotalOrderAsync(GameConnectionContext context, Packet packet)
    {
        byte index = PvpMatchingRequests.SelectCharacter(packet.Body);
        var notices = context.Session.CurrentPvpArena?.Room.Match is { } match
            ? arena.SelectTotal(context.Session, match.Id, index, Now) : [];
        arena.Publish(notices);
        log.Write("pvp_total_selection", new { Connection = context.Id, Index = index, Accepted = notices.Length != 0 });
        return Task.FromResult(CommandResult.Handled);
    }

    Task<CommandResult> ModeAsync(GameConnectionContext context, Packet packet)
    {
        byte value = PvpArenaRequests.Mode(packet.Body);
        if (value is not (1 or 2)) return Task.FromResult(CommandResult.Unhandled);
        if (context.Session.CurrentPvpArena is { } member)
            arena.Publish(arena.Configure(context.Session, value == 1 ? PvpArenaMode.Individual : PvpArenaMode.Team, member.Room.Map));
        return Task.FromResult(CommandResult.Handled);
    }

    Task<CommandResult> MapAsync(GameConnectionContext context, Packet packet)
    {
        ushort map = PvpArenaRequests.Map(packet.Body);
        if (context.Session.CurrentPvpArena is { } member)
            arena.Publish(arena.Configure(context.Session, member.Room.Mode, map));
        return Task.FromResult(CommandResult.Handled);
    }
}
