using DfoServer.Game.Settings;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class SettingsProjection(ClientSettingsService settings)
{
    public ClientSettingsReceipt Capture(GameConnectionContext context) => context.Session.LoadClientSettings(settings);

    public async Task InitializeAsync(GameConnectionContext context, ClientSettingsReceipt receipt)
    {
        bool CanSend() => context.Session.CanPublishClientSettings(receipt);
        // 角色实体已建立后再恢复保存值；没有存档时沿用客户端默认。
        if (!receipt.Settings.GameOptions.IsDefault)
            await context.Notify(NotiPacketType.GAME_OPTION, SettingsPackets.GameOptions(receipt.Settings.GameOptions), "account_settings_initialize", CanSend);
        if (!receipt.Settings.CharacterOptions.IsDefault)
            await context.Notify(NotiPacketType.CHARACTER_OPTION, SettingsPackets.CharacterOptions(receipt.Settings.CharacterOptions), "character_settings_initialize", CanSend);
        if (!receipt.Settings.GameOptions.IsDefault)
            await context.Notify(NotiPacketType.CHARAC_INVISIBLE_FALGS, SettingsPackets.Visibility(receipt.CharacterId, receipt.VisibilityFlags), "character_visibility_initialize", CanSend);
    }
}
