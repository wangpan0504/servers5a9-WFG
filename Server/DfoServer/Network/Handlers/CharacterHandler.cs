using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Infrastructure;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.GameWorld.Items;

namespace DfoServer.Network.Handlers;

internal sealed partial class CharacterHandler(CharacterCatalog characters, TownProjection projection, ServerLog log, GoldLimitCatalog goldLimits,
    SkillLearningCatalog? learning = null, TutorialCatalog? tutorials = null, AdventureProjection? adventure = null,
    ExpertJobScriptCatalog? expertJobs = null, AdventureWeeklyProjection? weekly = null,
    AvatarPresetProjection? avatarPresets = null, AvatarPresetService? avatarPresetService = null,
    DfoServer.Game.Session.OnlineSessionDirectory? online = null, SocialProjection? social = null, DfoServer.Game.Pvp.PvpPrivateCharacterService? privateCharacters = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.SELECT_CHARACTER, SelectAsync, nameof(CharacterHandler));
        commands.Register(CmdPacketType.CHECK_DOUBLE_CHARACTER_NAME, CheckNameAsync, nameof(CharacterHandler));
        commands.Register(CmdPacketType.CREATE_CHARACTER, CreateAsync, nameof(CharacterHandler));
        commands.Register(CmdPacketType.CHECK_PVP_PRIVATE_CHARACTER, CheckPvpPrivateAsync, nameof(CharacterHandler));
        commands.Register(CmdPacketType.DELETE_CHARACTER, DeleteAsync, nameof(CharacterHandler));
        commands.Register(CmdPacketType.CHANGE_CHARAC_SLOT, ChangeSlotAsync, nameof(CharacterHandler));
        commands.Register(CmdPacketType.GET_USERINFO, RosterAsync, nameof(CharacterHandler));
        commands.Register(CmdPacketType.CHARAC_VIEW_HIDDEN_CHARAC_INFO, HiddenRosterAsync, nameof(CharacterHandler));
        commands.Register(CmdPacketType.STORY_DIGEST_UPDATE, StoryDigestAsync, nameof(CharacterHandler));
    }

    Task<CommandResult> StoryDigestAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 0) return Task.FromResult(CommandResult.Unhandled);
        var character = context.Session.AcceptStoryDigest();
        log.Write("story_digest_accepted", new { Connection = context.Id, Character = character?.Id,
            character?.Level, character?.AcceptedStoryLevel, character?.Revision });
        // 匹配客户端在发1446前已更新本地值；这里只保存，下次选角由1382回放。
        return Task.FromResult(CommandResult.Handled);
    }

    async Task<CommandResult> SelectAsync(GameConnectionContext context, Packet request)
    {
        try { return await SelectCoreAsync(context, request); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("select_failed", new { Connection = context.Id, Reason = error.GetType().Name, error.Message });
            throw;
        }
    }

    async Task<CommandResult> SelectCoreAsync(GameConnectionContext context, Packet request)
    {
        // 原生换频道直接发C2S4；普通选角先请求C2S8，返回选角也有已推送名册。
        bool resumeTown = !context.RosterRequested && !context.SelectionRosterPushed;
        ushort selection=CharacterRequests.Select(request.Body);
        if(context.Session.PvpPrivateSelectionBlocked(selection))
        {
            // 023428B1的96明确显示当前频道不允许此操作，失败无额外body且重置选角UI。
            await context.Reply(CmdPacketType.SELECT_CHARACTER,[0,96,0],"pvp_private_channel_rejected");
            return CommandResult.Handled;
        }
        context.Session.SelectFromRosterIndex(selection, goldLimits, resumeTown);
        bool nativeSwitch = resumeTown;
        resumeTown = context.Session.TownSnapshotResumed;
        if (learning is not null)
        {
            var initialization = context.Session.InitializeLearning(learning, expertJobs);
            if (initialization != DfoServer.Game.Skills.SkillLearningFailure.None)
                log.Write("skill_info_unavailable", new { Connection = context.Id, Reason = initialization.ToString() });
        }
        // 1号橱窗是基础预设：槽0缺失或从未选中过橱窗时按当前穿戴落库并记为当前橱窗。
        bool createdBase = avatarPresetService is not null && context.Session.InitializeAvatarPresets(avatarPresetService);
        // 进入场景后不再保留返回选角阶段的重复 C2S8 标记。
        context.SelectionRosterPushed = false;
        var c = context.Session.Selected!;
        log.Write("selected", new { Connection = context.Id, Character = c.Id, c.Position, c.Return, c.Revision, ResumeTown = resumeTown, NativeSwitch = nativeSwitch });
        if (createdBase)
            log.Write("avatar_preset_base", new { Connection = context.Id, Character = c.Id,
                Capacity = c.Inventory.AvatarPresets.Capacity, Presets = c.Inventory.AvatarPresets.Presets.Length,
                Revision = c.Revision });
        await projection.EnterAsync(context, resumeTown);
        if (expertJobs is not null && context.Session.Selected is { ExpertJobType: >= 1 and <= 4 } selected)
            await context.Notify(NotiPacketType.EXPERT_JOB_INFO, ExpertJobPackets.Info(selected, expertJobs, includeQualifications: true), "expert_job_info");
        context.Session.MarkReady();
        // 预设容量与列表必须早于窗口打开；与每周配置同属就绪后、任务核对前的固定包序。
        if (avatarPresets is not null) await avatarPresets.DeliverAsync(context);
        // 每周地下城固定在入城完成包之后、任务核对与同屏加入之前下发，包序稳定且不依赖其他投影结果。
        if (weekly is not null) await weekly.DeliverAsync(context, includeReset: true);
        social?.Initialize(context);
        await projection.ReconcileQuestsAsync(context);
        projection.Join(context);
        return CommandResult.Handled;
    }

    async Task<CommandResult> CheckNameAsync(GameConnectionContext context, Packet request)
    {
        string name = CharacterRequests.CheckName(request.Body);
        bool exists = context.Session.NameExists(name);
        await context.Reply(CmdPacketType.CHECK_DOUBLE_CHARACTER_NAME, CharacterPackets.NameCheck(exists), "name_check");
        return CommandResult.Handled;
    }

    async Task<CommandResult> CheckPvpPrivateAsync(GameConnectionContext context,Packet request)
    {
        var parsed=CharacterRequests.PvpPrivateCheck(request.Body);
        DfoServer.Game.Characters.NewCharacter? plan=null;
        string reason="unavailable";
        privateCharacters?.TryPrepare("PrivateCheck",parsed.Job,parsed.Grow,DateTimeOffset.UtcNow,out plan,out reason);
        var receipt=context.Session.CheckPvpPrivateCreation(plan);
        log.Write("pvp_private_create_check",new {Connection=context.Id,parsed.Job,parsed.Grow,receipt.Allowed,Reason=reason});
        await context.Reply(CmdPacketType.CHECK_PVP_PRIVATE_CHARACTER,CharacterPackets.PvpPrivateChecked(receipt.Allowed),
            "pvp_private_create_check",()=>context.Session.CanPublishPvpPrivateCreationCheck(receipt));
        return CommandResult.Handled;
    }

    async Task<CommandResult> CreateAsync(GameConnectionContext context, Packet request)
    {
        var parsed = CharacterRequests.ParseCreate(request.Body);
        DfoServer.Game.Characters.CharacterCreationReceipt receipt;
        if(parsed.IsPvpPrivate)
        {
            if(privateCharacters is null || !privateCharacters.TryPrepare(parsed.Name,parsed.Job,parsed.PvpGrow,DateTimeOffset.UtcNow,out var plan,out _) ||
                context.Session.CreatePvpPrivateCharacterForClient(plan!) is not {} committed)
            {
                // 023433F2失败仅消费统一error u16；19结束本次创建，不伪造任何角色或资格提示。
                await context.Reply(CmdPacketType.CREATE_CHARACTER,[0,19,0],"pvp_private_create_rejected");
                return CommandResult.Handled;
            }
            receipt=committed;
        }
        else receipt = context.Session.CreateCharacterForClient(parsed.Name, characters.Get(parsed.Job), learning, tutorials?.Get(parsed.Job));
        bool CanSend() => context.Session.CanPublishCreation(receipt);
        await context.Reply(CmdPacketType.CREATE_CHARACTER, CharacterPackets.Created(receipt.Created), "create_committed", CanSend);
        await context.Notify(NotiPacketType.USERINFO, CharacterPackets.Roster(receipt.Roster.Entries.ToArray(), receipt.Roster.Capacity), "created_roster", CanSend);
        context.Session.MarkRosterPublished(receipt.Roster);
        return CommandResult.Handled;
    }

    async Task<CommandResult> DeleteAsync(GameConnectionContext context, Packet request)
    {
        var parsed = CharacterRequests.Delete(request.Body);
        var original = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        var result = context.Session.DeleteCharacterFromRoster(parsed.RosterIndex, parsed.ConfirmationName);
        log.Write(result.Accepted ? "character_deleted" : "character_delete_rejected", new
        {
            Connection = context.Id, RosterIndex = parsed.RosterIndex, result.Reason,
            Character = result.Receipt?.Deleted.Id, Slot = result.Receipt?.Deleted.Slot,
            Revision = result.Receipt?.Deleted.Revision, result.SelectionEpoch
        });
        bool CanSend() => result.Receipt is { } committed ? context.Session.CanPublishDeletion(committed) : context.Owner.Execute(() =>
            context.Session.SelectionEpoch == original.SelectionEpoch && context.Session.Phase == original.Phase &&
            ReferenceEquals(context.Session.Selected, original.Selected));
        await context.Reply(CmdPacketType.DELETE_CHARACTER, CharacterPackets.Deleted(result),
            result.Accepted ? "character_delete_committed" : "character_delete_rejected", CanSend);
        if (result.Receipt is { } receipt)
        {
            await context.Notify(NotiPacketType.USERINFO, CharacterPackets.Roster(receipt.Roster.Entries.ToArray(), receipt.Roster.Capacity), "deleted_roster", CanSend);
            context.Session.MarkRosterPublished(receipt.Roster);
            // ACK6的消费者随后发空C2S645；新名册已主动下发，后续显式C2S8可以正常刷新。
            context.RosterRequested = true;
            context.SelectionRosterPushed = false;
        }
        return CommandResult.Handled;
    }

    async Task<CommandResult> ChangeSlotAsync(GameConnectionContext context, Packet request)
    {
        var parsed = CharacterRequests.ChangeSlot(request.Body);
        var result = context.Session.ChangeCharacterSlots(parsed.IndexA, parsed.IndexB);
        log.Write(result.Accepted ? "character_slot_change" : "character_slot_change_rejected", new
        {
            Connection = context.Id, IndexA = parsed.IndexA, IndexB = parsed.IndexB, result.Reason, result.SelectionEpoch,
            Count = result.Receipt?.Roster.Entries.Length
        });
        if (result.Receipt is not { } receipt) return CommandResult.Handled;
        bool CanSend() => context.Session.CanPublishSlotChange(receipt);
        if (result.Reason == "committed")
        {
            // 客户端拖动后本地行序已与本次互换一致：先安装新行序，再只回ACK。
            // 不再整份回推名册，避免客户端清空并重建名册列表把滚动条复位到第一行。
            context.Session.MarkRosterPublished(receipt.Roster);
            await context.Reply(CmdPacketType.CHANGE_CHARAC_SLOT, CharacterPackets.SlotChanged(), "character_slot_ack", CanSend);
            return CommandResult.Handled;
        }
        // 同索引/名册不匹配没有写库，仍用当前名册覆盖客户端本地拖动结果，使其退出拖动状态。
        await context.Reply(CmdPacketType.CHANGE_CHARAC_SLOT, CharacterPackets.SlotChanged(), "character_slot_ack", CanSend);
        await context.Notify(NotiPacketType.USERINFO, CharacterPackets.Roster(receipt.Roster.Entries.ToArray(), receipt.Roster.Capacity),
            "character_slot_roster", CanSend);
        context.Session.MarkRosterPublished(receipt.Roster);
        return CommandResult.Handled;
    }

    async Task<CommandResult> HiddenRosterAsync(GameConnectionContext context, Packet request)
    {
        CharacterRequests.HiddenRoster(request.Body);
        if (!context.Session.CheckState(DfoServer.Game.Session.UserState.CharacterSelect)) return CommandResult.Unhandled;
        long epoch = context.Session.SelectionEpoch;
        bool Guard() => context.Session.SelectionEpoch == epoch && context.Session.CheckState(DfoServer.Game.Session.UserState.CharacterSelect);
        await context.Reply(CmdPacketType.CHARAC_VIEW_HIDDEN_CHARAC_INFO, CharacterPackets.HiddenRoster(), "hidden_character_roster", Guard);
        if (adventure is not null && Guard()) await adventure.InformationAsync(context);
        return CommandResult.Handled;
    }

    async Task<CommandResult> RosterAsync(GameConnectionContext context, Packet request)
    {
        if (!CharacterRequests.IsRoster(request.Body))
            return await OtherPlayerAsync(context, request);
        if (context.SelectionRosterPushed)
        {
            // 返回选角已主动发送名册；客户端仍会按原流程补发一次 C2S8，此帧只确认请求已消费。
            context.SelectionRosterPushed = false;
            context.RosterRequested = true;
            return CommandResult.Handled;
        }
        if (context.RosterRequested)
        {
            var roster = context.Session.PrepareRoster();
            await context.Notify(NotiPacketType.USERINFO, CharacterPackets.Roster(roster.Entries.ToArray(), roster.Capacity), "requested_roster",
                () => context.Session.CanPublishRoster(roster));
            context.Session.MarkRosterPublished(roster);
        }
        context.RosterRequested = true;
        return CommandResult.Handled;
    }
}
