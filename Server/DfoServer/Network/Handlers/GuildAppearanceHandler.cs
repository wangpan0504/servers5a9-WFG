using DfoServer.Game.Guild;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class GuildAppearanceHandler(GuildAppearanceService service, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.CHANGE_GUILD_MARK, ChangeAsync, nameof(GuildAppearanceHandler));
        commands.Register(CmdPacketType.SET_GUILD_FLAG_AURA, ChangeAsync, nameof(GuildAppearanceHandler));
    }

    async Task<CommandResult> ChangeAsync(GameConnectionContext context, Packet request)
    {
        if (service.Capture(context.Session) is not { } actor) return CommandResult.Handled;
        bool aura = request.Opcode == (ushort)CmdPacketType.SET_GUILD_FLAG_AURA;
        GuildAppearanceCommit result;
        try
        {
            var change = aura ? GuildAppearanceRequests.Aura(request.Body) : GuildAppearanceRequests.Mark(request.Body);
            result = context.BlocksTownActions ? new(34) : service.Change(actor, change);
        }
        catch (InvalidDataException) { result = new(34); }
        // 装饰请求没有同号CMD回调，保存后由权威公会信息通知更新。
        if (!aura)
            await context.Reply(CmdPacketType.CHANGE_GUILD_MARK, GuildAppearancePackets.MarkResult(result.Error),
                "guild_mark", actor.IsCurrent);
        if (result.Success) service.Publish(result);
        log.Write("guild_appearance", new { Connection = context.Id, Character = actor.CharacterId,
            Guild = result.Refresh?.GuildId, Aura = aura, result.Error, Mark = result.Appearance?.Mark,
            AuraMark = result.Appearance?.AuraMark, AuraFrame = result.Appearance?.AuraFrame });
        return CommandResult.Handled;
    }
}
