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
        commands.Register(CmdPacketType.SAVE_GAME_OPTION_2, SaveHotkeysAsync, nameof(SettingsHandler));
        commands.Register(CmdPacketType.CHANGE_EMOTION, ChangeEmotionAsync, nameof(SettingsHandler));
    }

    async Task<CommandResult> ChangeEmotionAsync(GameConnectionContext context, Packet packet)
    {
        if (packet.Body.Length != 2) throw new InvalidDataException("心情请求必须是完整u16");
        ushort emotion = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packet.Body);
        long epoch = context.Session.SelectionEpoch;
        var phase = context.Session.Phase;
        var result = context.Session.ChangeEmotion(settings, emotion);
        if (result.Character is not { } character)
        {
            await context.Reply(CmdPacketType.CHANGE_EMOTION, SettingsPackets.Emotion(result.Error, success: false), "emotion_rejected");
            return CommandResult.Handled;
        }
        bool Guard() => context.Session.Ready && context.Session.SelectionEpoch == epoch && context.Session.Phase == phase &&
            ReferenceEquals(context.Session.Selected, character);
        log.Write("emotion_saved", new { Connection = context.Id, Character = character.Id, Emotion = emotion, result.Changed });
        await context.Reply(CmdPacketType.CHANGE_EMOTION, SettingsPackets.Emotion(emotion), "emotion_saved", Guard);
        // ACK确认UI选择；完整Actor只在城镇实体上下文刷新，其他场景的持久心情在后续入城回放。
        if (result.Changed && phase is SessionPhase.Town or SessionPhase.MapSelect)
        {
            await context.Notify(NotiPacketType.USERINFO,
                TownPackets.Actor(character, context.Channel, adventureName: context.Session.ReadAdventureName(),
                    premiumPcRoom: context.Session.PremiumPcRoom), "emotion_actor", Guard);
            context.Owner.Execute(() =>
            {
                if (Guard() && phase == SessionPhase.Town && !context.BlocksTownActions) projection.Refresh(context);
                return true;
            });
        }
        return CommandResult.Handled;
    }

    Task<CommandResult> SaveHotkeysAsync(GameConnectionContext context, Packet packet)
    {
        var text = SettingsRequests.Hotkeys(packet.Body);
        bool changed = context.Session.SaveHotkeys(settings, text);
        log.Write("client_settings_saved", new { Connection = context.Id, Scope = "account_hotkeys", Bytes = text.Length, Changed = changed });
        // 454会重置原生键位模型，仅在选角回放；保存时不发ACK或即时刷新。
        return Task.FromResult(CommandResult.Handled);
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
