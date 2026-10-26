using DfoServer.Game.Settings;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class SettingsProjection(ClientSettingsService settings)
{
    public ClientSettingsReceipt Capture(GameConnectionContext context) => context.Session.LoadClientSettings(settings);

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
