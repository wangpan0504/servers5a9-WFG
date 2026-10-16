using DfoServer.Game.Settings;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class SettingsProjection(ClientSettingsService settings)
{
    public ClientSettingsReceipt Capture(GameConnectionContext context) => context.Session.LoadClientSettings(settings);

    public Task PreloadGameOptionsAsync(GameConnectionContext context, ClientSettingsReceipt receipt) =>
        context.Notify(NotiPacketType.GAME_OPTION, receipt.Settings.GameOptions.IsDefault
                ? SettingsPackets.GameOptionsInitialization() : SettingsPackets.GameOptions(receipt.Settings.GameOptions),
            "account_settings_before_player", () => context.Session.CanPublishClientSettings(receipt));

    public async Task InitializeAsync(GameConnectionContext context, ClientSettingsReceipt receipt)
    {
        bool CanSend() => context.Session.CanPublishClientSettings(receipt);
        // 后段仅恢复已保存值；无存档的模型已在实体前初始化，不能再次重置。
        if (!receipt.Settings.GameOptions.IsDefault)
            await context.Notify(NotiPacketType.GAME_OPTION, SettingsPackets.GameOptions(receipt.Settings.GameOptions), "account_settings_initialize", CanSend);
        if (!receipt.Settings.CharacterOptions.IsDefault)
            await context.Notify(NotiPacketType.CHARACTER_OPTION, SettingsPackets.CharacterOptions(receipt.Settings.CharacterOptions), "character_settings_initialize", CanSend);
        if (!receipt.Settings.Hotkeys.IsDefault)
            await context.Notify(NotiPacketType.HOTKEY_OPTION, SettingsPackets.Hotkeys(receipt.HotkeyProfile, receipt.Settings.Hotkeys), "account_hotkeys_initialize", CanSend);
        if (!receipt.Settings.GameOptions.IsDefault)
            await context.Notify(NotiPacketType.CHARAC_INVISIBLE_FALGS, SettingsPackets.Visibility(receipt.CharacterId, receipt.VisibilityFlags), "character_visibility_initialize", CanSend);
    }
}
