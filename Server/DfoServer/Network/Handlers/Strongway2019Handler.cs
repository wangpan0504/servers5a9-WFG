using DfoServer.Game.Strongway2019;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;
using DfoServer.Network.Projection;

namespace DfoServer.Network.Handlers;

internal sealed class Strongway2019Handler(Strongway2019MatchingService matching,
    Strongway2019Projection projection, Strongway2019MatchingEntryService? entries = null,
    DungeonCatalog? dungeons = null, DungeonRoomCatalog? rooms = null, DungeonMonsterDropCatalog? drops = null,
    ItemCatalog? items = null, EpicPieceCatalog? pieces = null, Strongway2019Catalog? catalog = null,
    TimeProvider? clock = null, Func<GameConnectionContext, Task>? depart = null,
    Func<DungeonEntryPlan, Func<DungeonPlannedMonster, DungeonDropGenerationPlan>>? prepareDrops = null,
    OnlineSessionDirectory? online = null) : IGameCommandModule
{
    readonly SemaphoreSlim maintenance = new(1, 1);
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.DPL_E_DUNGEON_SYMBOL_SYNC, SymbolAsync, nameof(Strongway2019Handler));
        commands.Register(CmdPacketType.MATCHING_DUNGEON_EXIT, ExitAsync, nameof(Strongway2019Handler));
    }

    internal async Task<CommandResult> QueueAsync(GameConnectionContext context, PvpMatchingRequest request)
    {
        var party = context.Session.CurrentParty?.Invoke();
        var actor = context.Session.CaptureSocialActor();
        bool Held(SemaphoreSlim gate) => context.DispatchGates?.Holds(context, gate) == true;
        var admissions = party?.Members.Select(member => member.Actor.Session.PrepareStrongway2019Admission(5360, matched: true)).ToArray();
        var ticket = Strongway2019Requests.IsMatching(request) && actor is not null && admissions is { Length: 4 } &&
            admissions.All(row => row is not null) ? matching.Queue(context.Session, admissions.Select(row => row!).ToArray(),
                context.Activity?.BlocksTownActions == true, Held) : null;
        if (ticket is null)
        {
            await context.Reply(CmdPacketType.REQUEST_INTEGRATED_MATCHING, PvpMatchingPackets.Rejected(0x17), "strongway_matching_rejected");
            return CommandResult.Handled;
        }
        // 原生末字段只将1视为true；当前不启用通用PK排除选项，显式发送0。
        await context.Reply(CmdPacketType.REQUEST_INTEGRATED_MATCHING, Strongway2019Packets.MatchingAccepted(0),
            "strongway_matching_queued", actor!.IsCurrent);
        if (matching.ReserveNext(ticket, Held) is { } pair) await EnterAsync(pair, Held);
        return CommandResult.Handled;
    }

    async Task EnterAsync(Strongway2019MatchingPair pair, Func<SemaphoreSlim, bool>? holds = null)
    {
        if (entries is null || dungeons is null || rooms is null || catalog is null ||
            entries.Enter(pair, dungeons, rooms, drops, items, pieces, prepareDrops, holdsCommandGate: holds) is not { } entered) return;
        uint deadline = checked((uint)(clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds() + (uint)catalog.DungeonStartDelay);
        await projection.WaitingAsync(entered.Match, deadline);
        await PartyDungeonProjection.PublishAsync(entered.Notices);
    }

    // Runtime共用的一秒维护；所有跨队取门仍为零等待，忙门留下的原Pair下轮重试。
    public async Task MaintainAsync(CancellationToken stop)
    {
        if (!maintenance.Wait(0)) return;
        try
        {
            var snapshot = matching.Snapshot();
            var attempted = new HashSet<Guid>();
            foreach (var ticket in snapshot.Tickets)
            {
                stop.ThrowIfCancellationRequested();
                var pair = ticket.Pair ?? matching.ReserveNext(ticket);
                if (pair is not null && attempted.Add(pair.Id)) await EnterAsync(pair);
            }
            foreach (var match in matching.Snapshot().Matches)
            {
                stop.ThrowIfCancellationRequested();
                match.Competition.Tick();
                await projection.TerminalAsync(match);
                if (catalog is not null && projection.IsReturnDue(match, catalog.ReturnVillageDelay))
                    await projection.ReturnAsync(match);
            }
            // 普通/英雄也使用原在线目录与原Run时钟；无收包连接仍由同一秒维护推进退场。
            foreach (var character in online?.Snapshot() ?? [])
            {
                stop.ThrowIfCancellationRequested();
                if (character.Phase is not (SessionPhase.Loading or SessionPhase.Dungeon) ||
                    online!.Find(character.Account) is not { } session ||
                    session.CaptureStrongway2019NormalTimeout() is not { } timeout ||
                    session.Strongway2019NormalTimeoutArrived is not { } returnToTown) continue;
                await returnToTown(timeout);
            }
        }
        finally { maintenance.Release(); }
    }
    internal async Task<CommandResult> CancelAsync(GameConnectionContext context, Strongway2019MatchingControlRequest request)
    {
        var ticket = context.Session.CurrentStrongway2019Ticket;
        bool accepted = request.IsMatching && ticket is not null && matching.Cancel(context.Session, ticket,
            gate => context.DispatchGates?.Holds(context, gate) == true);
        await context.Reply(CmdPacketType.CANCEL_INTEGRATED_MATCHING, accepted ? PvpMatchingPackets.Cancelled() :
            PvpMatchingPackets.Rejected(0x17), accepted ? "strongway_matching_cancelled" : "strongway_matching_cancel_rejected");
        return CommandResult.Handled;
    }

    async Task<CommandResult> SymbolAsync(GameConnectionContext context, Packet packet)
    {
        var request = Strongway2019Requests.Symbol(packet.Body);
        var match = context.Session.CurrentStrongway2019Match;
        var run = context.Session.PartyDungeon is { } instance ? context.Session.CaptureStrongway2019Run(instance) : null;
        var member = match?.Competition.Participants.SingleOrDefault(row => ReferenceEquals(row.Run, run));
        if (match is null || run is null || member is null || request.Side != member.Side)
            return CommandResult.Handled;
        if (request.Kind == 7 && request.RawValue is 3 or 4)
        {
            if (match.Competition.ConfirmOpening(run, run.Scene.Identity, checked((int)request.RawValue)) == DungeonRunOutcome.Accepted)
                await projection.SymbolAsync(match, request.Side, 7, request.RawValue);
        }
        else if (request.Kind <= 4 && match.Competition.UseSkill(run, run.Scene.Identity, 1 - member.Side,
            checked((int)request.Kind), request.RawValue) is { Outcome: DungeonRunOutcome.Accepted, Effect: { } effect })
            await projection.SymbolAsync(match, checked((uint)effect.SourceSide), checked((uint)effect.Kind), effect.Value);
        else if (request.Kind == 6 && request.RawValue is >= 1 and <= 100 &&
            CanDisplayMonsterHp(context, match, member, catalog))
            // 原生6是当前配置怪物百分比。仅允许原队长提交非零HUD遥测，不进入战斗裁决。
            await projection.SymbolAsync(match, request.Side, 6, request.RawValue);
        await projection.TerminalAsync(match);
        return CommandResult.Handled;
    }

    static bool CanDisplayMonsterHp(GameConnectionContext context, Strongway2019Match match,
        Strongway2019Participant source, Strongway2019Catalog? activity)
    {
        if (match.Competition.Snapshot.Phase != Strongway2019CompetitionPhase.Running) return false;
        var party = source.Side == 0 ? match.Pair.First.Party : match.Pair.Second.Party;
        var instance = source.Side == 0 ? match.First : match.Second;
        if (!party.Members.Any(member => member.Slot == party.Leader &&
            ReferenceEquals(member.Actor.Session, context.Session) && member.Actor.IsCurrent())) return false;
        var scene = source.Run.Scene;
        if (activity is null || !activity.MapMonsters.TryGetValue(scene.Map.Id, out int target)) return false;
        return party.Members.All(member => member.Actor.IsCurrent() &&
            ReferenceEquals(member.Actor.Session.CurrentStrongway2019Match, match) &&
            ReferenceEquals(member.Actor.Session.PartyDungeon, instance) &&
            member.Actor.Session.CaptureStrongway2019Run(instance) is { Phase: SinglePlayerDungeonPhase.Dungeon } current &&
            match.Competition.Participants.Any(row => row.Side == source.Side && ReferenceEquals(row.Run, current) &&
                row.Identity.CharacterId == current.Scene.Identity.CharacterId &&
                row.Identity.InstanceId == current.Scene.Identity.InstanceId &&
                row.Identity.TaskAttempt == current.Scene.Identity.TaskAttempt &&
                row.Identity.QuestId == current.Scene.Identity.QuestId) &&
            current.Scene.Map.Id == scene.Map.Id && current.Scene.Map.Position == scene.Map.Position &&
            // 原权威已确认目标死亡后，非零遥测不能把刚发布的零血条抬回；无配置目标也不造血条。
            current.CurrentMonsterPlan.Monsters.Any(monster => monster.MonsterId == target &&
                current.Snapshot.LivingMonsters.Contains(monster.ObjectKey)));
    }

    async Task<CommandResult> ExitAsync(GameConnectionContext context, Packet packet)
    {
        var request = Strongway2019Requests.MatchingControl(packet.Body);
        if (!request.IsMatching || context.Session.CurrentStrongway2019Match is not { } match ||
            context.Session.PartyDungeon is not { } instance || context.Session.CaptureStrongway2019Run(instance) is not { } run)
            return CommandResult.Handled;
        match.Competition.Exit(run, run.Scene.Identity);
        // 先让获胜队持久化奖励，再进入原副本退场；522没有已证ACK，不添加通用成功包。
        await projection.TerminalAsync(match);
        if (depart is not null) await depart(context);
        return CommandResult.Handled;
    }
}
