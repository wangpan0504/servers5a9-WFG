using DfoServer.Game.Settings;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;
using DfoServer.Network.Projection;
using DfoServer.Network.Builders;
using DfoServer.Game.Session;

namespace DfoServer.Network.Handlers;

internal sealed class SettingsHandler(ClientSettingsService settings, ServerLog log, TownProjection projection,
    DfoServer.GameWorld.Characters.ChatEmoticonCatalog? emoticons = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.INFORM_NOTICE_2ND, SeenNoticeAsync, nameof(SettingsHandler));
        commands.Register(CmdPacketType.SAVE_GAME_OPTION_1, SaveGameAsync, nameof(SettingsHandler));
        commands.Register(CmdPacketType.SAVE_CHARACTER_OPTION, SaveCharacterAsync, nameof(SettingsHandler));
        commands.Register(CmdPacketType.SAVE_GAME_OPTION_2, SaveHotkeysAsync, nameof(SettingsHandler));
        commands.Register(CmdPacketType.SAVE_GAME_OPTION_QUICKCHATTING, SaveQuickChatAsync, nameof(SettingsHandler));
        commands.Register(CmdPacketType.SAVE_GAME_OPTION_CHATTING_EMOTICON, SaveChatEmoticonsAsync, nameof(SettingsHandler));
        commands.Register(CmdPacketType.SAVE_RECENT_EMOTICON_LIST, SaveRecentEmoticonsAsync, nameof(SettingsHandler));
        commands.Register(CmdPacketType.CHANGE_EMOTION, ChangeEmotionAsync, nameof(SettingsHandler));
        commands.Register(CmdPacketType.SAVE_QUEST_NOTIFY, SaveQuestNotifyAsync, nameof(SettingsHandler));
        commands.Register(CmdPacketType.UPDATE_NPC_BOOKMARK_INFO, BookmarkAsync, nameof(SettingsHandler));
    }

    Task<CommandResult> SeenNoticeAsync(GameConnectionContext context, Packet packet)
    {
        context.Session.SaveSeenNotice(settings, SettingsRequests.SeenNotice(packet.Body));
        // 02642740在发送前已更新本地集合；503没有ACK消费者，不再伪造确认包。
        return Task.FromResult(CommandResult.Handled);
    }

    async Task<CommandResult> BookmarkAsync(GameConnectionContext context, Packet packet)
    {
        var request = SettingsRequests.NpcBookmark(packet.Body);
        var receipt = context.Session.UpdateNpcBookmark(settings, request.Add, request.NpcId);
        log.Write("npc_bookmark_saved", new { Connection = context.Id, request.Add, request.NpcId, Accepted = receipt is not null });
        if (receipt is null)
        {
            await context.Reply(CmdPacketType.UPDATE_NPC_BOOKMARK_INFO, [0, 19, 0], "npc_bookmark_rejected");
            return CommandResult.Handled;
        }
        bool Guard() => context.Session.CanPublishClientSettings(receipt);
        // 先安装列表，再由成功ACK刷新已打开的辞典窗口。
        await context.Notify(NotiPacketType.NPC_BOOKMARK_INFO, SettingsPackets.NpcBookmarks(receipt.Settings.NpcBookmarks), "npc_bookmarks_saved", Guard);
        await context.Reply(CmdPacketType.UPDATE_NPC_BOOKMARK_INFO, [1], "npc_bookmark_success", Guard);
        return CommandResult.Handled;
    }

    Task<CommandResult> SaveQuestNotifyAsync(GameConnectionContext context, Packet packet)
    {
        var entries = SettingsRequests.QuestNotify(packet.Body);
        var result = context.Session.SaveQuestNotify(settings, entries);
        log.Write("client_settings_saved", new { Connection = context.Id, Scope = "character_quest_notify",
            Count = packet.Body[0], result.Accepted, result.Changed });
        // 书签由原选角四槽恢复；不发送未核证的专用ACK或覆盖当前界面的列表。
        return Task.FromResult(CommandResult.Handled);
    }

    async Task<CommandResult> SaveChatEmoticonsAsync(GameConnectionContext context, Packet packet)
    {
        var macros = SettingsRequests.ChatEmoticons(packet.Body);
        if (emoticons is not null && macros.Any(row => !emoticons.Contains(row.Id)))
            throw new InvalidDataException("聊天表情编号不在当前资源名册");
        bool changed = context.Session.SaveChatEmoticons(settings, macros);
        var receipt = context.Session.LoadClientSettings(settings);
        log.Write("client_settings_saved", new { Connection = context.Id, Scope = "account_chat_emoticons", Count = macros.Length, Changed = changed });
        await context.Reply(CmdPacketType.SAVE_GAME_OPTION_CHATTING_EMOTICON, [1], "chat_emoticons_saved",
            () => context.Session.CanPublishClientSettings(receipt));
        if (context.ChatEmoticons is { } chat) await chat.RefreshAsync(force: true, saved: receipt);
        return CommandResult.Handled;
    }

    Task<CommandResult> SaveRecentEmoticonsAsync(GameConnectionContext context, Packet packet)
    {
        var entries = SettingsRequests.RecentEmoticons(packet.Body);
        if (emoticons is not null)
            for (int offset = 0; offset < entries.Length; offset += sizeof(uint))
            {
                uint id = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan()[offset..]);
                if (id != 0 && !emoticons.Contains((ushort)id))
                    throw new InvalidDataException("最近表情编号不在当前资源名册");
            }
        bool changed = context.Session.SaveRecentEmoticons(settings, entries);
        log.Write("client_settings_saved", new { Connection = context.Id, Scope = "account_recent_emoticons", Changed = changed });
        // ACK1447是空消费者；保存不回推列表，避免覆盖客户端刚更新的最近使用顺序。
        return Task.FromResult(CommandResult.Handled);
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
                TownPackets.Actor(character, context.Session.ReadPvpGrade(character), context.Channel, adventureName: context.Session.ReadAdventureName(),
                    premiumPcRoom: context.Session.PremiumPcRoom, returningAdventurerType: context.Session.ReturningAdventurerType, marriage: context.MarriageAppearance), "emotion_actor", Guard);
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
        log.Write("client_settings_saved", new { Connection = context.Id, Scope = "character_hotkeys", Bytes = text.Length, Changed = changed });
        // 454会重置原生键位模型，仅在选角回放；保存时不发ACK或即时刷新。
        return Task.FromResult(CommandResult.Handled);
    }

    Task<CommandResult> SaveQuickChatAsync(GameConnectionContext context, Packet packet)
    {
        var (bank, text) = SettingsRequests.QuickChat(packet.Body);
        bool changed = context.Session.SaveQuickChat(settings, bank, text);
        log.Write("client_settings_saved", new { Connection = context.Id, Scope = "account_quick_chat", Bank = bank, Bytes = text.Length, Changed = changed });
        // 368没有专用成功ACK；重新登录时由173按两个银行回放。
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
