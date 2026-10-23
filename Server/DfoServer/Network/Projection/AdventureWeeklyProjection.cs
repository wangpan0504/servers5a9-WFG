using System.Collections.Immutable;
using DfoServer.Game.Adventure;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 每周地下城配置下发：就绪后与791保存成功后都先清列表（720）再回放5槽（721）。
// 读库失败降级为全未选择并记日志，不阻塞入城，也不把旧排列当默认值。
// Full 模式在721之后再主动下发整份719（key取本频道服务器序号）；Slots 模式由客户端789/790自行拉取。
internal sealed class AdventureWeeklyProjection(AdventureWeeklyMode mode, ServerLog log)
{
    public AdventureWeeklyMode Mode => mode;

    public async Task DeliverAsync(GameConnectionContext context, bool includeReset = false)
    {
        if (mode == AdventureWeeklyMode.Off) return;
        var selected = context.Session.Selected;
        if (selected is null) return;
        long epoch = context.Session.SelectionEpoch;
        bool Guard() => context.Session.SelectionEpoch == epoch && ReferenceEquals(context.Session.Selected, selected) &&
            context.Session.Ready;
        ImmutableArray<byte> slots;
        bool degraded = false;
        try
        {
            slots = context.Session.ReadAdventureWeeklyConfig();
        }
        catch (Exception error)
        {
            degraded = true;
            slots = AdventureWeekly.EmptySlots;
            log.Write("adventure_weekly_config_read_failed", new { Connection = context.Id, Error = error.GetType().Name });
        }
        if (includeReset || degraded || slots.All(slot => slot == AdventureWeekly.Empty))
            await context.Notify(NotiPacketType.WEEKLY_DUNGEON_INFO_RESETDAILY, AdventurePackets.WeeklyReset(),
                "adventure_weekly_reset", Guard);
        await context.Notify(NotiPacketType.WEEKLY_DUNGEON_INFO_CONFIG, AdventurePackets.WeeklyConfig(slots),
            "adventure_weekly_config", Guard);
        if (mode != AdventureWeeklyMode.Full) return;
        try
        {
            var rows = context.Session.ReadAdventureWeeklyRoster();
            await context.Notify(NotiPacketType.WEEKLY_DUNGEON_INFO,
                AdventurePackets.WeeklyDungeonInfo(context.Channel.ServerIndex, rows), "adventure_weekly_info", Guard);
        }
        catch (Exception error)
        {
            log.Write("adventure_weekly_roster_read_failed", new { Connection = context.Id, Error = error.GetType().Name });
        }
    }
}
