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
        // 攻坚商店按声明读 list44/45 的持有量，不读 1460 的每周已获得数。
        // 不渝之咏没有活动页登记，仍并入同一槽序，供随后的 14 增量使用。
        int[] calendarIds = pages.Where(page => page.Visible).SelectMany(page => page.ShowItemIds).Distinct().Order()
            .Where(id => id > 0 && items.Get(id).AccountMaterial is not null).ToArray();
        int[] ids = AccountMaterialProjectionIds(context, pages);
        var previous = context.ActivityCalendarItems;
        bool same = previous is { } current && current.Epoch == epoch && current.ItemIds.SequenceEqual(ids);
        if (ids.Length == 0 && previous is not { ItemIds.Length: > 0 }) return;
        bool clearPrevious = previous is { ItemIds.Length: > 0, Epoch: var previousEpoch } && previousEpoch == epoch && !same;
        bool introduceBalance = !same && ids.Any(id => character.Inventory.AccountWallet.Material(id).Balance > 0);
        // 活动页始终全量替换。没有活动页时，不把全零的攻坚材料插进选角的 108/639 之间。
        context.ActivityCalendarItems = (epoch, ids);
        if (calendarIds.Length == 0 && !clearPrevious && !introduceBalance) return;
        var bodies = CalendarLists(ids).Select(row => ActivityCalendarItemPackets.Full(row.List,
            character.Inventory.AccountWallet, row.ItemIds)).ToArray();
        foreach (byte[] body in bodies)
            await context.Notify(NotiPacketType.ITEM_LIST, body, "activity_calendar_items",
                () => canSend?.Invoke() != false && context.Session.SelectionEpoch == epoch &&
                    context.Session.Selected?.Id == character.Id && context.ActivityCalendarItems?.ItemIds == ids);
    }

    // 活动登记与攻坚额度材料去重后按模板升序固定槽；零余额不占全量包，增加时仍用原槽。
    int[] AccountMaterialProjectionIds(GameConnectionContext context, IReadOnlyList<EventInfoDesignPage> pages)
    {
        var selected = new HashSet<int>();
        foreach (int id in pages.Where(page => page.Visible).SelectMany(page => page.ShowItemIds))
            if (id > 0 && items!.Get(id).AccountMaterial is not null) selected.Add(id);
        foreach (int id in items!.RaidAccountMaterialLimits.Keys)
            if (items.TryGet(id, out var template) && template.AccountMaterial is not null) selected.Add(id);
        // 谋略战召唤界面直接查询list45；券和硬币并入共享槽序，领取、消耗及重连沿同一投影。
        if (context.MonsterMockBattle is { Enabled: true } battle)
            foreach (int id in battle.AccountMaterialIds)
                if (items.TryGet(id, out var template) && template.AccountMaterial is not null) selected.Add(id);
        return selected.Order().ToArray();
    }

    // 同一活动可以登记两种材料；模板按资源声明进入原生购买检查所查询的背包。
    IEnumerable<(byte List, int[] ItemIds)> CalendarLists(int[] ids)
    {
        foreach (byte list in new[] { ActivityCalendarItemPackets.RaidList, ActivityCalendarItemPackets.CommonList })
            yield return (list, ids.Where(id => items!.Get(id).AccountMaterial!.CommonInventory ==
                (list == ActivityCalendarItemPackets.CommonList)).ToArray());
    }
}
