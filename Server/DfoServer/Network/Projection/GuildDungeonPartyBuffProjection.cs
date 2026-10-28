using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class GuildDungeonPartyBuffProjection
{
    public static void Post(GameConnectionContext context, bool clear = false)
    {
        if (context.Session.PlanGuildDungeonPartyBuff(clear) is not { } receipt) return;
        // 队列内旧变化可能失效；最后一次变化清掉本功能全部档位，不能只删尚未真正发送的上一档。
        if (receipt.Removed.Length > 0)
            context.Sender.Post(0, (ushort)NotiPacketType.CHARACTER_DEL_BUFF,
                GuildEconomyPackets.RemovedBuffs(receipt.Removed), "guild_dungeon_party_buff_removed", receipt.IsCurrent);
        if (receipt.Added != 0)
            context.Sender.Post(0, (ushort)NotiPacketType.CHARACTER_ADD_BUFF,
                GuildEconomyPackets.AddedBuffs([new(Type: 0, Value: receipt.Added, ExpiresUnix: 0)], 0),
                "guild_dungeon_party_buff_added", receipt.IsCurrent);
    }
}
