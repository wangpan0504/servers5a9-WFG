using DfoServer.Game.Settings;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class SettingsProjection(ClientSettingsService settings, DfoServer.GameWorld.Characters.ChatEmoticonCatalog? emoticons = null)
{
    public ClientSettingsReceipt Capture(GameConnectionContext context) => context.Session.LoadClientSettings(settings);

    public ChatEmoticonProjection? CreateChatEmoticons(GameConnectionContext context) =>
        emoticons is null ? null : new(context, settings, emoticons);

    public async Task PreloadGameOptionsAsync(GameConnectionContext context, ClientSettingsReceipt receipt)
    {
        bool CanSend() => context.Session.CanPublishClientSettings(receipt);
        await context.Notify(NotiPacketType.GAME_OPTION, receipt.Settings.GameOptions.IsDefault
                ? SettingsPackets.GameOptionsInitialization(receipt.Settings.QuickChat0, receipt.Settings.QuickChat1)
                : SettingsPackets.GameOptions(receipt.Settings.GameOptions, receipt.Settings.QuickChat0, receipt.Settings.QuickChat1),
            "account_settings_before_player", CanSend);
        // 02ABE870创建光环时已读取角色选项48；后段回放仍保留依赖本人实体的选项应用。
        await context.Notify(NotiPacketType.CHARACTER_OPTION, SettingsPackets.CharacterOptions(receipt.Settings.CharacterOptions),
            "character_settings_before_player", CanSend);
    }

    public async Task InitializeAsync(GameConnectionContext context, ClientSettingsReceipt receipt)
    {
        bool CanSend() => context.Session.CanPublishClientSettings(receipt);
        await context.Notify(NotiPacketType.NPC_BOOKMARK_INFO, SettingsPackets.NpcBookmarks(receipt.Settings.NpcBookmarks),
            "npc_bookmarks_initialize", CanSend);
        if (context.ChatEmoticons is { } chat)
        {
            // 1384只安装最近列表；随后的411重建UI，填充第9至26格。
            await context.Notify(NotiPacketType.RECENT_EMOTICON_LIST, SettingsPackets.RecentEmoticons(receipt.Settings.RecentEmoticons),
                "recent_emoticons_initialize", CanSend);
            await chat.RefreshAsync(force: true, saved: receipt);
        }
        // 主区只恢复已保存值；角色选项在实体建立后安装，包括无存档时的光环显示初始化。
        if (!receipt.Settings.GameOptions.IsDefault)
            await context.Notify(NotiPacketType.GAME_OPTION, SettingsPackets.GameOptions(receipt.Settings.GameOptions,
                receipt.Settings.QuickChat0, receipt.Settings.QuickChat1), "account_settings_initialize", CanSend);
        if (!receipt.Settings.CharacterOptions.IsDefault)
            await context.Notify(NotiPacketType.CHARACTER_OPTION, SettingsPackets.CharacterOptions(receipt.Settings.CharacterOptions), "character_settings_initialize", CanSend);
        if (!receipt.Settings.Hotkeys.IsDefault)
            await context.Notify(NotiPacketType.HOTKEY_OPTION, SettingsPackets.Hotkeys(receipt.HotkeyProfile, receipt.Settings.Hotkeys), "account_hotkeys_initialize", CanSend);
        if (!receipt.Settings.GameOptions.IsDefault)
            await context.Notify(NotiPacketType.CHARAC_INVISIBLE_FALGS, SettingsPackets.Visibility(receipt.CharacterId, receipt.VisibilityFlags), "character_visibility_initialize", CanSend);
    }

}
