using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class GriefTowerProjection
{
    public static async Task SelectionAsync(GameConnectionContext context, DungeonCatalog? catalog,
        int legendDungeon, string eventName, Func<bool> guard)
    {
        var grief = catalog is null ? null : context.Session.GriefTowerSelection(catalog);
        if (grief is { } state)
            await context.Notify(NotiPacketType.TOWER_DUNGEON_CLEAR_INFO,GriefTowerPackets.ClearState(state.ClearedToday),
                "grief_tower_clear_state",guard);
        await DungeonEnterCountProjection.RefreshAsync(context, catalog, guard, onlyForSelection: true);
        await DazzlementTowerProjection.BindingsAsync(context, guard);
        RaidCombatProjection.Selection(context, guard);
        await DimensionEntranceProjection.RefreshAsync(context, guard);
        await DimensionEntranceProjection.AncientAsync(context, guard);
        await SelectionNoticeAsync(context, legendDungeon, eventName, guard,
            grief?.Layer ?? 0, context.Session.DespairSelectionLayer());
    }

    internal static async Task SelectionNoticeAsync(GameConnectionContext context, int legendDungeon,
        string eventName, Func<bool> guard, int griefLayer = 0, ushort despairLayer = 0)
    {
        // OP27名单只负责首版原图入侵标识；五档标签保护和第六按钮由配套客户端补丁处理。
        var beast = context.Session.CaptureBeastDungeonSelection();
        if (context.Beast is { } previousDisplay) await previousDisplay.RefreshAsync();
        bool CanSendSelection() => guard() && (beast is null || context.Session.CanPublishBeastDungeonSelection(beast));
        await context.Notify(NotiPacketType.ENTER_SELECT_DUNGEON,
            DungeonPackets.SelectionNotice(legendDungeon,griefLayer,despairLayer,
                context.Session.IncompleteHellQuestMemberSlots(),beast is null ? [] : [.. beast.Counts.Select(row => (int)row.Dungeon)]),
            eventName,CanSendSelection);
        if (beast is not null && context.Beast is { } display)
            await context.Sender.Send(0, BeastDisplayPackets.Opcode,
                BeastDisplayPackets.Selection(display.NextDisplaySerial(), display.DisplayConnection,
                    beast.Character.Id, beast.Day, beast.Counts.AsSpan()), "beast_display_selection", CanSendSelection);
    }
}
