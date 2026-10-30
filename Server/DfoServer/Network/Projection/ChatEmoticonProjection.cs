using DfoServer.Game.Session;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Characters;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 每连接只保存最后已下发的资格视图；真正资格和指令仍归角色/账号，不建立另一套在线状态。
internal sealed class ChatEmoticonProjection(GameConnectionContext context, ClientSettingsService settings, ChatEmoticonCatalog catalog)
{
    (ChatEmoticonEligibility Key, long Epoch)? announced;
    System.Collections.Immutable.ImmutableArray<ChatEmoticonEntry> lastEntries;

    public async Task RefreshAsync(bool force = false, ClientSettingsReceipt? saved = null)
    {
        var current = context.Session.ChatEmoticonEligibility(initializing: saved is not null);
        if (current is null)
        {
            announced = null;
            return;
        }
        if (!force && current == announced) return;
        var entries = catalog.Eligible(current.Value.Key);
        if (!force && announced?.Epoch == current.Value.Epoch && !lastEntries.IsDefault && entries.SequenceEqual(lastEntries))
        {
            announced = current;
            return;
        }
        var receipt = saved ?? context.Session.LoadClientSettings(settings);
        bool Guard() => context.Session.CanPublishClientSettings(receipt) && context.Session.ChatEmoticonEligibility(initializing: saved is not null) == current;
        if (!Guard()) return;
        if (!entries.IsEmpty)
        {
            bool sent = false;
            await context.Notify(NotiPacketType.EMOTICON_CONF_INFO, SettingsPackets.ChatEmoticons(entries, receipt.Settings.ChatEmoticons),
                "chat_emoticons_initialize", () => sent = Guard());
            if (!sent) return;
        }
        announced = current;
        lastEntries = entries;
    }
}
