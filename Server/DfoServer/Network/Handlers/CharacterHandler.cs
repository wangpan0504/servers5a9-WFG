using DfoServer.GameWorld.Characters;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Infrastructure;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.GameWorld.Items;

namespace DfoServer.Network.Handlers;

internal sealed class CharacterHandler(CharacterCatalog characters, TownProjection projection, ServerLog log, GoldLimitCatalog goldLimits,
    SkillLearningCatalog? learning = null, TutorialCatalog? tutorials = null, AdventureProjection? adventure = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.SELECT_CHARACTER, SelectAsync, nameof(CharacterHandler));
        commands.Register(CmdPacketType.CHECK_DOUBLE_CHARACTER_NAME, CheckNameAsync, nameof(CharacterHandler));
        commands.Register(CmdPacketType.CREATE_CHARACTER, CreateAsync, nameof(CharacterHandler));
        commands.Register(CmdPacketType.DELETE_CHARACTER, DeleteAsync, nameof(CharacterHandler));
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
        // 原生换频道直接发C2S4；普通选角先请求C2S8，返回选角也有已推送名册。
        bool resumeTown = !context.RosterRequested && !context.SelectionRosterPushed;
        context.Session.SelectFromRosterIndex(CharacterRequests.Select(request.Body), goldLimits, resumeTown);
        if (learning is not null)
        {
            var initialization = context.Session.InitializeLearning(learning);
            if (initialization != DfoServer.Game.Skills.SkillLearningFailure.None)
                log.Write("skill_info_unavailable", new { Connection = context.Id, Reason = initialization.ToString() });
        }
        // 进入场景后不再保留返回选角阶段的重复 C2S8 标记。
        context.SelectionRosterPushed = false;
        var c = context.Session.Selected!;
        log.Write("selected", new { Connection = context.Id, Character = c.Id, c.Position, c.Return, c.Revision, ResumeTown = resumeTown });
        await projection.EnterAsync(context, resumeTown);
        context.Session.MarkReady();
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

    async Task<CommandResult> CreateAsync(GameConnectionContext context, Packet request)
    {
        var parsed = CharacterRequests.ParseCreate(request.Body);
        var receipt = context.Session.CreateCharacterForClient(parsed.Name, characters.Get(parsed.Job), learning, tutorials?.Get(parsed.Job));
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
            return CommandResult.Unhandled;
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
