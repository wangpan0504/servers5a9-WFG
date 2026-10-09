using DfoServer.Infrastructure;
using DfoServer.Network.Protocol;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;

namespace DfoServer.Network.Handlers;

// 正式与诊断实例共用已经确认的选图界面入口，不负责建立副本实例。
internal sealed class DungeonSelectionHandler(TownProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.ENTER_SELECT_DUNGEON, EnterAsync, nameof(DungeonSelectionHandler));
        commands.Register(CmdPacketType.BACK_2_VILLAGE, ReturnAsync, nameof(DungeonSelectionHandler));
    }

    async Task<CommandResult> EnterAsync(GameConnectionContext context, Packet request)
    {
        var entry = DungeonRequests.ParseEntry(request.Body);
        if (context.Activity?.BlocksTownActions == true)
            return CommandResult.Unhandled;
        var continuation = context.Session.CompleteFormalDungeonContinuation();
        if (continuation.Pending)
        {
            if (continuation.Dungeon is { } entered)
            {
                await projection.DetachAsync(context);
                bool Guard() => context.Session.CanPublishFormalDungeon(entered);
                log.Write("formal_quest_continuation_enter",new { Connection = context.Id, entered.Entry.QuestId,
                    entered.Entry.DungeonId, entered.Snapshot.Identity });
                // 27隐藏旧Scene界面并切换场景模块；ACK15/state1不能替代，缺少它会保留旧结算页。
                await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON,DungeonPackets.SelectionOpened(),"formal_next_quest_entry_confirm",Guard);
                await context.Notify(NotiPacketType.USER_STATE,DungeonPackets.DungeonPlayerState(entered.Character.Id),"formal_next_quest_entry_state",Guard);
                await context.Notify(NotiPacketType.ENTER_SELECT_DUNGEON,DungeonPackets.EmptySelectNotice(),"formal_next_quest_scene_transition",Guard);
                await context.Notify(NotiPacketType.DUNGEON_INFO,DungeonPackets.Information(entered.Entry),"formal_next_quest_information",Guard);
                await context.Notify(NotiPacketType.START_MAP,DungeonPackets.StartMap(entered.Entry,entered.Monsters,entered.MapSeed),"formal_next_quest_start_map",Guard);
            }
            else if (continuation.Town is { } town)
                await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON,DungeonPackets.SelectionOpened(),"formal_next_quest_town_finalizer",
                    () => context.Session.CanPublishTravel(town));
            else if (continuation.Replay is { } replay)
                await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON,DungeonPackets.SelectionOpened(),"formal_next_quest_entry_replay",
                    () => context.Session.CanPublishFormalDungeon(replay));
            return CommandResult.Handled;
        }
        var replayBefore = context.Owner.Execute(() => (context.Session.Selected,context.Session.Ready,
            context.Session.SelectionEpoch,context.Session.Phase,Identity: context.Session.DungeonSnapshot?.Identity));
        var questReplaySelection = context.Session.BeginQuestReplayDungeonSelection(entry.DungeonIndex);
        if (questReplaySelection.Pending)
        {
            if (questReplaySelection.Receipt is { } selected)
            {
                if (!await projection.DetachForDungeonSelectionAsync(context,selected)) return CommandResult.Handled;
                bool Guard() => context.Session.CanPublishDungeonSelection(selected);
                await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON,DungeonPackets.SelectionOpened(),"quest_replay_selection_opened",Guard);
                await context.Notify(NotiPacketType.USER_STATE,DungeonPackets.DungeonPlayerState(selected.Character.Id),"quest_replay_selection_state",Guard);
            }
            else if (questReplaySelection.Replay is { } installed)
                await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON,DungeonPackets.SelectionOpened(),"quest_replay_selection_retry",
                    () => context.Session.CanPublishFormalDungeon(installed));
            else
                await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON,DungeonPackets.SelectionUnavailable(),"quest_replay_selection_rejected",
                    () => context.Owner.Execute(() => replayBefore.Ready && context.Session.Ready &&
                        context.Session.SelectionEpoch == replayBefore.SelectionEpoch && context.Session.Phase == replayBefore.Phase &&
                        ReferenceEquals(context.Session.Selected,replayBefore.Selected) && context.Session.DungeonSnapshot?.Identity == replayBefore.Identity));
            return CommandResult.Handled;
        }
        if (context.BlocksTownActions)
            return CommandResult.Unhandled;
        var receipt = context.Session.BeginDungeonSelection();
        if (!await projection.DetachForDungeonSelectionAsync(context, receipt))
            return CommandResult.Handled;
        bool CanSend() => context.Session.CanPublishDungeonSelection(receipt);
        // 正文 u32 为 0 是左侧入口打开选图；非 0 是已经选中的副本编号。再推空列表会把选图界面重新打开。
        uint dungeon = entry.DungeonIndex;
        log.Write("dungeon_entry", new { Connection = context.Id, Accepted = true, Dungeon = dungeon, Reason = dungeon == 0 ? "select-notice" : "dungeon-chosen", receipt.Character.Position, receipt.SelectionId });
        await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionOpened(), "dungeon_entry_opened", CanSend);
        // 同步客户端选图状态，触发旧城镇实体清理；仅切服务端阶段不会结束客户端旧动作。
        await context.Notify(NotiPacketType.USER_STATE, DungeonPackets.DungeonPlayerState(receipt.Character.Id), "dungeon_selection_state", CanSend);
        if (dungeon == 0)
            await context.Notify(NotiPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.EmptySelectNotice(), "dungeon_select_notice", CanSend);
        return CommandResult.Handled;
    }

    async Task<CommandResult> ReturnAsync(GameConnectionContext context, Packet request)
    {
        if (!DungeonRequests.IsSelectionReturn(request.Body) || context.BlocksTownActions)
            return CommandResult.Unhandled;
        var receipt = context.Session.ReturnFromDungeonSelection();
        log.Write("dungeon_selection_return", new { Connection = context.Id, Accepted = receipt is not null, receipt?.SelectionId });
        if (receipt is not null)
            await projection.ReturnFromDungeonSelectionAsync(context, receipt);
        return CommandResult.Handled;
    }
}
