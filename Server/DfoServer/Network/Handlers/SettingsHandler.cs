using DfoServer.Game.Settings;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;
using DfoServer.Network.Projection;
using DfoServer.Network.Builders;
using DfoServer.Game.Session;

namespace DfoServer.Network.Handlers;

internal sealed class SettingsHandler(ClientSettingsService settings, ServerLog log, TownProjection projection) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.SAVE_GAME_OPTION_1, SaveGameAsync, nameof(SettingsHandler));
        commands.Register(CmdPacketType.SAVE_CHARACTER_OPTION, SaveCharacterAsync, nameof(SettingsHandler));
    }

    async Task<CommandResult> SaveGameAsync(GameConnectionContext context, Packet packet)
    {
        var options = SettingsRequests.GameOptions(packet.Body);
        byte? previous = context.Session.Selected?.VisibilityFlags;
        bool changed = context.Session.SaveGameOptions(settings, options);
        log.Write("client_settings_saved", new { Connection = context.Id, Scope = "account", Bytes = options.Length, Changed = changed });
        if (context.Session.Selected is { } character && previous != character.VisibilityFlags)
        {
            var receipt = context.Session.LoadClientSettings(settings);
            await context.Notify(NotiPacketType.CHARAC_INVISIBLE_FALGS, SettingsPackets.Visibility(receipt.CharacterId, receipt.VisibilityFlags),
                "character_visibility_saved", () => context.Session.CanPublishClientSettings(receipt));
            if (context.Session.Ready && context.Session.Phase == SessionPhase.Town && !context.BlocksTownActions)
                projection.Refresh(context);
        }
        // 保存没有专用成功ACK，不回推打开的设置窗口所用的173/390。
        return CommandResult.Handled;
    }

    Task<CommandResult> SaveCharacterAsync(GameConnectionContext context, Packet packet)
    {
        var options = SettingsRequests.CharacterOptions(packet.Body);
        bool changed = context.Session.SaveCharacterOptions(settings, options);
        log.Write("client_settings_saved", new { Connection = context.Id, Scope = "character", Character = context.Session.Selected?.Id, Bytes = options.Length, Changed = changed });
        return Task.FromResult(CommandResult.Handled);
    }
}
