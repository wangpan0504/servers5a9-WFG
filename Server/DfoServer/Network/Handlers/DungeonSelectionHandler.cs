using DfoServer.Infrastructure;
using DfoServer.Network.Protocol;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Game.Session;

namespace DfoServer.Network.Handlers;

// 正式与诊断实例共用已经确认的选图界面入口，不负责建立副本实例。
internal sealed class DungeonSelectionHandler(TownProjection projection, ServerLog log,
    DfoServer.GameWorld.Characters.TutorialCatalog? tutorials = null,
    DfoServer.GameWorld.Dungeons.DungeonCatalog? dungeons = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.ENTER_SELECT_DUNGEON, EnterAsync, nameof(DungeonSelectionHandler));
        commands.Register(CmdPacketType.BACK_2_VILLAGE, ReturnAsync, nameof(DungeonSelectionHandler), UserStateRequirement.WorldReady);
    }

    async Task<CommandResult> EnterAsync(GameConnectionContext context, Packet request)
    {
        var entry = DungeonRequests.ParseEntry(request.Body);
        if (!context.Session.CheckState(UserStateRequirement.WorldReady)) return CommandResult.Unhandled;
        if (context.Activity?.BlocksTownActions == true)
            return CommandResult.Unhandled;
        if (context.Session.Phase == SessionPhase.Dungeon)
        {
            var hellSelection = entry.DungeonIndex == 0 ? context.Session.BeginClearedHellSelection() : null;
            if (hellSelection is null) return CommandResult.Unhandled;
            int target = dungeons is null ? 0 : context.Session.PrepareLegendDungeonSelection(dungeons);
            log.Write("formal_hell_selection", new { Connection = context.Id, hellSelection.Character.Id, hellSelection.SelectionId, LegendDungeon = target });
            if (!await projection.DetachForDungeonSelectionAsync(context, hellSelection)) return CommandResult.Handled;
            bool Guard() => context.Session.CanPublishDungeonSelection(hellSelection);
            await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionOpened(), "formal_hell_selection_opened", Guard);
            await context.Notify(NotiPacketType.USER_STATE, DungeonPackets.DungeonPlayerState(hellSelection.Character.Id), "formal_hell_selection_state", Guard);
            await context.Notify(NotiPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.EmptySelectNotice(target), "formal_hell_select_notice", Guard);
            return CommandResult.Handled;
        }
        var continuation = context.Session.CompleteFormalDungeonContinuation(entry.DungeonIndex);
        if (continuation.Pending)
        {
            if (continuation.Selection is { } selected)
            {
                if (!await projection.DetachForDungeonSelectionAsync(context,selected)) return CommandResult.Handled;
                bool Guard() => context.Session.CanPublishDungeonSelection(selected);
                log.Write("formal_quest_continuation_selection",new { Connection = context.Id, entry.DungeonIndex,
                    continuation.Opened, selected.SelectionId });
                // 27隐藏旧Scene界面并切换场景模块；ACK15/state1不能替代，缺少它会保留旧结算页。
                await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON,DungeonPackets.SelectionOpened(),"formal_next_quest_entry_confirm",Guard);
                if (continuation.Opened)
                {
                    await context.Notify(NotiPacketType.USER_STATE,DungeonPackets.DungeonPlayerState(selected.Character.Id),"formal_next_quest_entry_state",Guard);
                    await context.Notify(NotiPacketType.ENTER_SELECT_DUNGEON,DungeonPackets.EmptySelectNotice(),"formal_next_quest_scene_transition",Guard);
                }
            }
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
                if (!context.Session.ConfirmNewDungeonSelection(selected)) return CommandResult.Handled;
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
        var receipt = entry.DungeonIndex == 7142 ? context.Session.BeginClassExperienceSelection() :
            tutorials is not null ? context.Session.BeginTutorialSelection(entry.DungeonIndex, tutorials) : null;
        if (entry.DungeonIndex == 7142 && receipt is null) return CommandResult.Unhandled;
        receipt ??= context.Session.BeginDungeonSelection();
        if (receipt is null)
        {
            log.Write("dungeon_entry", new { Connection = context.Id, Accepted = false, Dungeon = entry.DungeonIndex,
                Reason = "gate-return-position-unavailable", Position = replayBefore.Selected?.Position });
            await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionUnavailable(), "dungeon_entry_rejected",
                () => context.Owner.Execute(() => replayBefore.Ready && context.Session.Ready &&
                    context.Session.SelectionEpoch == replayBefore.SelectionEpoch && context.Session.Phase == replayBefore.Phase &&
                    ReferenceEquals(context.Session.Selected,replayBefore.Selected) && context.Session.DungeonSnapshot?.Identity == replayBefore.Identity));
            return CommandResult.Handled;
        }
        if (!context.Session.ConfirmNewDungeonSelection(receipt)) return CommandResult.Handled;
        int legendTarget = dungeons is null ? 0 : context.Session.PrepareLegendDungeonSelection(dungeons);
        if (!await projection.DetachForDungeonSelectionAsync(context, receipt))
            return CommandResult.Handled;
        bool CanSend() => context.Session.CanPublishDungeonSelection(receipt);
        // 正文 u32 为 0 是左侧入口打开选图；非 0 是已经选中的副本编号。再推空列表会把选图界面重新打开。
        uint dungeon = entry.DungeonIndex;
        log.Write("dungeon_entry", new { Connection = context.Id, Accepted = true, Dungeon = dungeon, Reason = dungeon == 0 ? "select-notice" : "dungeon-chosen", receipt.Character.Position, receipt.SelectionId,
            LegendDungeon = legendTarget });
        await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionOpened(), "dungeon_entry_opened", CanSend);
        // 同步客户端选图状态，触发旧城镇实体清理；仅切服务端阶段不会结束客户端旧动作。
        await context.Notify(NotiPacketType.USER_STATE, DungeonPackets.DungeonPlayerState(receipt.Character.Id), "dungeon_selection_state", CanSend);
        // #233 实验：打开选图前刷新强袭模式每日信息（跨日后客户端不会自行更新今日索引）。
        if (dungeon == 0)
            await context.Notify(NotiPacketType.ASSAULT_MODE_DAILY_INFO, AssaultModePackets.Today(TimeProvider.System, context.Session.AssaultDailyCounts(TimeProvider.System.GetLocalNow())), "assault_mode_daily_info", CanSend);
        if (dungeon == 0 || receipt.Tutorial && receipt.OpensClientSelector)
            await context.Notify(NotiPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.EmptySelectNotice(legendTarget), "dungeon_select_notice", CanSend);
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
