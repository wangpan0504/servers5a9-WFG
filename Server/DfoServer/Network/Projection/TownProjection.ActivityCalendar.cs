using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed partial class TownProjection
{
    // 每次目录发布均恢复真实余额；同模板跨活动去重，初始/换角/关活动只用13静默替换。
    internal async Task CalendarItemsAsync(GameConnectionContext context, IReadOnlyList<EventInfoDesignPage> pages,
        Func<bool>? canSend = null)
    {
        if (items is null || context.Session.Selected is not { } character || canSend?.Invoke() == false) return;
        long epoch = context.Session.SelectionEpoch;
        int[] ids = pages.Where(page => page.Visible).SelectMany(page => page.ShowItemIds).Distinct().Order()
            .Where(id => items.Get(id).AccountMaterial is not null).ToArray();
        if (ids.Length == 0 && context.ActivityCalendarItems is not { ItemIds.Length: > 0 }) return;
        var bodies = CalendarLists(ids).Select(row => ActivityCalendarItemPackets.Full(row.List,
            character.Inventory.AccountWallet, row.ItemIds)).ToArray();
        context.ActivityCalendarItems = (epoch, ids);
        foreach (byte[] body in bodies)
            await context.Notify(NotiPacketType.ITEM_LIST, body, "activity_calendar_items",
                () => canSend?.Invoke() != false && context.Session.SelectionEpoch == epoch &&
                    context.Session.Selected?.Id == character.Id && context.ActivityCalendarItems?.ItemIds == ids);
    }

    // 同一活动可以登记两种材料；模板按资源声明进入原生购买检查所查询的背包。
    IEnumerable<(byte List, int[] ItemIds)> CalendarLists(int[] ids)
    {
        foreach (byte list in new[] { ActivityCalendarItemPackets.RaidList, ActivityCalendarItemPackets.CommonList })
            yield return (list, ids.Where(id => items!.Get(id).AccountMaterial!.CommonInventory ==
                (list == ActivityCalendarItemPackets.CommonList)).ToArray());
    }
}
