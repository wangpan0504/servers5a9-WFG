using DfoServer.Infrastructure;
using DfoServer.Network.Protocol;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Development.Dungeons;

internal sealed class DungeonProbeModule : IGameCommandModule
{
    readonly ServerLog log;
    readonly DungeonCatalog probeCatalog;
    readonly DungeonEntryProbeMode probeStage;
    readonly DungeonRoomCatalog? roomProbeCatalog;
    readonly TownProjection? projection;

    public DungeonProbeModule(ServerLog log, DungeonCatalog catalog, DungeonEntryProbeMode stage, DungeonRoomCatalog? rooms = null, TownProjection? projection = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (stage is <= DungeonEntryProbeMode.Disabled or > DungeonEntryProbeMode.FinishLoadingMonsters)
            throw new ArgumentOutOfRangeException(nameof(stage));
        if (stage == DungeonEntryProbeMode.FinishLoadingMonsters && rooms is null)
            throw new ArgumentException("怪物诊断需要房间资源", nameof(rooms));
        this.log = log;
        probeCatalog = catalog;
        probeStage = stage;
        roomProbeCatalog = rooms;
        this.projection = projection;
    }

    static DungeonProbeState Probe(GameConnectionContext context) =>
        context.Activity as DungeonProbeState ?? throw new InvalidOperationException("诊断模块缺少对应连接状态");

    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.SELECT_DUNGEON, SelectAsync, nameof(DungeonProbeModule));
        commands.Register(CmdPacketType.FINISH_LOADING, FinishLoadingAsync, nameof(DungeonProbeModule));
        commands.Register(CmdPacketType.GIVEUP_GAME, ReturnToTownAsync, nameof(DungeonProbeModule));
        commands.Register(CmdPacketType.MOVE_MAP, MoveAsync, nameof(DungeonProbeModule));
        commands.Register(CmdPacketType.DIE_MONSTER, DeathAsync, nameof(DungeonProbeModule));
    }

    async Task<CommandResult> MoveAsync(GameConnectionContext context, Packet request)
    {
        var probe = Probe(context);
        if (!DungeonRequests.TryParseMove(request.Body, out var move)) return CommandResult.Unhandled;
        DungeonEntryPlan? nextPlan = null;
        DungeonMonsterPlan? nextMonsters = null;
        byte[]? body = null;
        var run = context.Owner.Execute(() =>
        {
            var active = ActiveRun(context);
            var target = active?.Target(move!.X, move.Y);
            if (active is null || target is null || roomProbeCatalog is null) return null;
            try
            {
                nextPlan = active.Entry with { StartMap = target };
                nextMonsters = active.Cached(target) ?? DungeonMonsterPlan.Create(roomProbeCatalog, target, nextPlan.CharacterId,
                    checked((ushort)probe.NextObjectKey), nextPlan.BasisLevel, playerTeam: nextPlan.PlayerTeam);
                body = DungeonEntryProbe.BuildStartMap(nextPlan, nextMonsters);
                active.Enter(target, nextMonsters);
                if (nextMonsters.Monsters.Count > 0)
                    probe.NextObjectKey = Math.Max(probe.NextObjectKey, nextMonsters.Monsters.Max(monster => monster.ObjectKey) + 1);
                probe.Plan = nextPlan;
                probe.Monsters = nextMonsters;
                probe.LoadingReplied = false;
                return active;
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or OverflowException)
            {
                log.Write("dungeon_probe_move_rejected", new { Connection = context.Id, error.Message });
                return null;
            }
        });
        log.Write("dungeon_probe_move", new { Connection = context.Id, Accepted = run is not null, Requested = move,
            run?.InstanceId, run?.RoomRevision, Plan = nextPlan, Monsters = nextMonsters });
        if (run is null) return CommandResult.Unhandled;
        int roomRevision = run.RoomRevision;
        await context.Reply(CmdPacketType.MOVE_MAP, [1], "probe_move_map_ack");
        context.Owner.Execute(() =>
        {
            if (!ReferenceEquals(ActiveRun(context), run) || run.RoomRevision != roomRevision)
                throw new InvalidOperationException("Dungeon probe room changed");
            return true;
        });
        await context.Notify(NotiPacketType.START_MAP, body!, "probe_next_room");
        return CommandResult.Handled;
    }

    async Task<CommandResult> DeathAsync(GameConnectionContext context, Packet request)
    {
        var probe = Probe(context);
        if (!DungeonRequests.TryParseDeath(request.Body, out var death)) return CommandResult.Unhandled;
        var result = context.Owner.Execute(() =>
        {
            var run = ActiveRun(context);
            if (run is null || death!.Reporter != run.Entry.CharacterId) return DungeonDeathReport.Rejected;
            return run.ReportDeath(death.ObjectKey);
        });
        log.Write("dungeon_probe_death", new { Connection = context.Id, Report = death, Result = result.ToString(),
            probe.Run?.InstanceId, probe.Run?.RoomRevision });
        if (result == DungeonDeathReport.Rejected) return CommandResult.Unhandled;
        if (result == DungeonDeathReport.Accepted)
            await context.Notify(NotiPacketType.DIE_MONSTER, DungeonEntryProbe.BuildDeath(checked((ushort)death!.ObjectKey)), "probe_monster_death");
        return CommandResult.Handled;
    }

    static DungeonProbeRun? ActiveRun(GameConnectionContext context)
    {
        var probe = Probe(context);
        var character = context.Session.Selected;
        var run = probe.Run;
        return context.BlocksTownActions && character is not null && run is not null &&
            run.Matches(character.Id, character.Revision, context.Session.SelectionEpoch) ? run : null;
    }
    async Task<CommandResult> SelectAsync(GameConnectionContext context, Packet request)
    {
        var probe = Probe(context);
        if (!DungeonRequests.TryParseSelection(request.Body, out var selection))
            return CommandResult.Unhandled;
        var state = context.Owner.Execute(() => new
        {
            Character = context.Session.Selected?.Id,
            context.Session.Ready,
            context.Session.Phase,
            context.Session.SelectionEpoch
        });
        string reason = "not-ready";
        DungeonEntryPlan? plan = null;
        DungeonMonsterPlan? monsters = null;
        byte[]? startMap = null;
        bool prepared = context.Owner.Execute(() =>
        {
            var character = context.Session.Selected;
            if (character is null || !context.Session.Ready || context.Session.SelectionEpoch != state.SelectionEpoch)
                return false;
            // 诊断范围绑定已实收的普通单人 3145 选择形状；未知模式不作为同一候选。
            if (selection.Unknown5 != 0 || selection.Unknown7 != 0 || selection.Unknown8 != 0 || selection.Unknown9 != ushort.MaxValue ||
                selection.Unknown11 != 0 || selection.Unknown15 != 0 || selection.Unknown20 != 0 || selection.Unknown21 != 0)
            {
                reason = "probe-request-shape-unsupported";
                return false;
            }
            if (probe.SelectionEpoch == state.SelectionEpoch)
            {
                reason = "probe-already-sent-for-selection";
                return false;
            }
            if (!DungeonEntryPlan.TryCreate(probeCatalog, character, selection.DungeonIndex, selection.Unknown16,
                selection.Parameter, out plan, out reason))
                return false;
            if (probeStage == DungeonEntryProbeMode.FinishLoadingMonsters)
            {
                if (roomProbeCatalog is null)
                {
                    reason = "room-resources-unavailable";
                    return false;
                }
                try
                {
                    monsters = DungeonMonsterPlan.Create(roomProbeCatalog, plan!.StartMap, character.Id, checked((ushort)probe.NextObjectKey),
                        plan.BasisLevel, playerTeam: plan.PlayerTeam);
                    startMap = DungeonEntryProbe.BuildStartMap(plan, monsters);
                }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException or OverflowException)
                {
                    reason = "room-monsters-unsupported: " + error.Message;
                    return false;
                }
            }
            probe.SelectionEpoch = state.SelectionEpoch;
            probe.Plan = null;
            probe.Monsters = monsters;
            probe.Run = monsters is null ? null : new DungeonProbeRun(plan!, monsters, state.SelectionEpoch);
            if (monsters is { Monsters.Count: > 0 }) probe.NextObjectKey = monsters.Monsters.Max(monster => monster.ObjectKey) + 1;
            probe.LoadingReplied = false;
            probe.ReturnStarted = false;
            probe.ReturnedToTown = false;
            return true;
        });
        log.Write("dungeon_entry_probe", new
        {
            Connection = context.Id, Stage = probeStage.ToString(), Prepared = prepared, Reason = reason,
            Request = selection, State = state, Plan = plan, Monsters = monsters
        });
        if (!prepared)
            return CommandResult.Unhandled;
        // 候选只在显式诊断实例发送；仍不宣告准入成功、加载完成或任务达成。
        await context.Notify(NotiPacketType.DUNGEON_INFO, DungeonEntryProbe.BuildInformation(plan!), "probe_dungeon_information");
        if (probeStage is DungeonEntryProbeMode.StartMap or DungeonEntryProbeMode.FinishLoading or DungeonEntryProbeMode.FinishLoadingMonsters)
        {
            context.Owner.Execute(() =>
            {
                if (context.Session.SelectionEpoch != state.SelectionEpoch || context.Session.Selected?.Id != plan!.CharacterId ||
                    context.Session.Selected.Revision != plan.CharacterRevision)
                    throw new InvalidOperationException("Dungeon probe selection changed");
                return true;
            });
            await context.Notify(NotiPacketType.START_MAP, startMap ?? DungeonEntryProbe.BuildStartMap(plan!, 1), "probe_dungeon_start_map");
            context.Owner.Execute(() =>
            {
                if (context.Session.SelectionEpoch == state.SelectionEpoch && context.Session.Selected?.Id == plan!.CharacterId &&
                    context.Session.Selected.Revision == plan.CharacterRevision)
                    probe.Plan = plan;
                return true;
            });
        }
        return CommandResult.Handled;
    }

    async Task<CommandResult> FinishLoadingAsync(GameConnectionContext context, Packet request)
    {
        var probe = Probe(context);
        // 027FC480 的当前发送点是两个零 u32；其他宽度或值保留未知，不发送候选。
        if (request.Body.Length != 8 || request.Body.Any(value => value != 0))
            return CommandResult.Unhandled;
        bool accepted = context.Owner.Execute(() =>
        {
            var plan = probe.Plan;
            if (probeStage is not (DungeonEntryProbeMode.FinishLoading or DungeonEntryProbeMode.FinishLoadingMonsters) || plan is null || probe.LoadingReplied ||
                !context.BlocksTownActions || context.Session.Selected?.Id != plan.CharacterId || context.Session.Selected.Revision != plan.CharacterRevision)
                return false;
            // 发送失败不重跑本次收尾。返回选角后由新的 epoch 重新建立诊断上下文。
            probe.LoadingReplied = true;
            return true;
        });
        log.Write("dungeon_probe_finish_loading", new { Connection = context.Id, Accepted = accepted, Stage = probeStage.ToString(), context.Session.SelectionEpoch });
        if (!accepted)
            return CommandResult.Unhandled;
        await context.Reply(CmdPacketType.FINISH_LOADING, [1], "probe_finish_loading_ack");
        context.Owner.Execute(() =>
        {
            if (!context.BlocksTownActions || context.Session.Selected?.Id != probe.Plan!.CharacterId ||
                context.Session.Selected.Revision != probe.Plan.CharacterRevision)
                throw new InvalidOperationException("Dungeon probe selection changed");
            return true;
        });
        await context.Notify(NotiPacketType.FINISH_LOADING, DungeonEntryProbe.BuildFinishLoading(), "probe_finish_loading_notice");
        ushort characterId = context.Owner.Execute(() =>
        {
            if (!context.BlocksTownActions || context.Session.Selected?.Id != probe.Plan!.CharacterId ||
                context.Session.Selected.Revision != probe.Plan.CharacterRevision)
                throw new InvalidOperationException("Dungeon probe selection changed");
            return probe.Plan.CharacterId;
        });
        await context.Notify(NotiPacketType.USER_STATE, DungeonEntryProbe.BuildPlayerState(characterId), "probe_dungeon_player_state");
        context.Owner.Execute(() => { ActiveRun(context)?.Loaded(); return true; });
        return CommandResult.Handled;
    }

    async Task<CommandResult> ReturnToTownAsync(GameConnectionContext context, Packet request)
    {
        var probe = Probe(context);
        if (request.Body.Length != 0)
            return CommandResult.Unhandled;
        var character = context.Owner.Execute(() =>
        {
            var selected = context.Session.Selected;
            var plan = probe.Plan;
            if (probeStage is not (DungeonEntryProbeMode.FinishLoading or DungeonEntryProbeMode.FinishLoadingMonsters) || !context.BlocksTownActions || !probe.LoadingReplied ||
                probe.ReturnStarted || selected is null || plan is null || selected.Id != plan.CharacterId || selected.Revision != plan.CharacterRevision)
                return null;
            // 诊断从未提交副本坐标；返回进入前的城镇快照，不写任务或清图事实。
            probe.ReturnStarted = true;
            probe.Run?.End();
            return selected;
        });
        log.Write("dungeon_probe_return", new { Connection = context.Id, Accepted = character is not null, context.Session.SelectionEpoch });
        if (character is null)
            return CommandResult.Unhandled;
        await context.Reply(CmdPacketType.GIVEUP_GAME, [1], "probe_giveup_ack");
        context.Owner.Execute(() =>
        {
            if (!context.BlocksTownActions || context.Session.Selected?.Id != character.Id || context.Session.Selected.Revision != character.Revision)
                throw new InvalidOperationException("Dungeon probe selection changed");
            return true;
        });
        // NOTI24 的当前模块 3 分支负责离开副本；不提前发送城镇状态改变其分支。
        await context.Notify(NotiPacketType.AREA_USERS, TownPackets.Transition(character), "probe_return_town_transition");
        var returned = context.Owner.Execute(() =>
        {
            if (context.BlocksTownActions && context.Session.Selected?.Id == character.Id && context.Session.Selected.Revision == character.Revision)
            {
                // 诊断42已投影原计划的位置，只结束共用选择阶段，不追加132的落点提交。
                var receipt = context.Session.ReturnFromDungeonSelection(restorePosition: false);
                // 诊断从不建立正式选择实例，回执为空也要结束诊断闸门，否则返城后城镇写操作会一直被拦到重选。
                probe.ReturnedToTown = true;
                return receipt;
            }
            return null;
        });
        if (returned is not null)
            projection?.Join(context, () => context.Session.CanPublishDungeonSelection(returned, returning: true));
        return CommandResult.Handled;
    }
}
