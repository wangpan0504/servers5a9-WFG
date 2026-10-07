using DfoServer.GameWorld.Characters;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Infrastructure;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.GameWorld.Items;

namespace DfoServer.Network.Handlers;

internal sealed class CharacterHandler(CharacterCatalog characters, TownProjection projection, ServerLog log, GoldLimitCatalog goldLimits,
    SkillLearningCatalog? learning = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.SELECT_CHARACTER, SelectAsync, nameof(CharacterHandler));
        commands.Register(CmdPacketType.CHECK_DOUBLE_CHARACTER_NAME, CheckNameAsync, nameof(CharacterHandler));
        commands.Register(CmdPacketType.CREATE_CHARACTER, CreateAsync, nameof(CharacterHandler));
        commands.Register(CmdPacketType.GET_USERINFO, RosterAsync, nameof(CharacterHandler));
    }

    async Task<CommandResult> SelectAsync(GameConnectionContext context, Packet request)
    {
        context.Session.Select(CharacterRequests.Select(request.Body), goldLimits);
        if (learning is not null)
        {
            var initialization = context.Session.InitializeLearning(learning);
            if (initialization != DfoServer.Game.Skills.SkillLearningFailure.None)
                log.Write("skill_info_unavailable", new { Connection = context.Id, Reason = initialization.ToString() });
        }
        // 进入场景后不再保留返回选角阶段的重复 C2S8 标记。
        context.SelectionRosterPushed = false;
        var c = context.Session.Selected!;
        log.Write("selected", new { Connection = context.Id, Character = c.Id, c.Position, c.Return, c.Revision });
        await projection.EnterAsync(context);
        context.Session.MarkReady();
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
        var created = context.Session.CreateCharacter(parsed.Name, characters.Get(parsed.Job), learning);
        await context.Reply(CmdPacketType.CREATE_CHARACTER, CharacterPackets.Created(created), "create_committed");
        await context.Notify(NotiPacketType.USERINFO, CharacterPackets.Roster(context.Session.Roster()), "created_roster");
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
            await context.Notify(NotiPacketType.USERINFO, CharacterPackets.Roster(context.Session.Roster()), "requested_roster");
        context.RosterRequested = true;
        return CommandResult.Handled;
    }
}
