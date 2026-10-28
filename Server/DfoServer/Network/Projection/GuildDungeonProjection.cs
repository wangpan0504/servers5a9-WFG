using DfoServer.Game.Guild;
using DfoServer.GameWorld.Guilds;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class GuildDungeonProjection(GuildDungeonCatalog catalog)
{
    public void Post(GameConnectionContext context, GuildPanel panel, Func<bool> guard)
    {
        if (panel.DungeonProgress is not { } progress || progress.GuildId != panel.Guild.Id) return;
        context.Sender.Post(0, (ushort)NotiPacketType.GUILD_DUNGEON_INFO,
            GuildDungeonPackets.Progress(catalog, progress), "guild_dungeon_progress",
            () => guard() && context.Session.Selected?.Guild?.Id == progress.GuildId);
    }
}
