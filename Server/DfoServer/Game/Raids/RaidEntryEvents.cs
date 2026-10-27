namespace DfoServer.Game.Raids;

// GM 时段表到客户端团队频道三张卡的对应。ARAD 01711690 逐卡刷新：
//   卡 0 卢克：任务 2903、事件 282（0x11A）、团本类型 1；
//   卡 1 超时空漩涡：任务 8672、事件 2449（0x991）、团本类型 2；
//   卡 2 普雷·伊希斯：任务 8935、事件 414（0x19E）、团本类型 3。
// imgDay（「非可入场时间」）在 026BD6C0(事件) 为假时显示；事件集合来自 NOTI EVENT_INFO(108) 的整表替换。
// 团队频道列表构建 017128D0 对 type 0x20/0x2D/0x32 频道也按同一事件与任务筛选。
internal static class RaidEntryEvents
{
    public readonly record struct Card(string Name, ushort EventId, byte RaidType, int Quest);

    public static readonly Card[] Cards =
    [
        new("luke", 282, 1, 2903),
        new("hyperspace", 2449, 2, 8672),
        new("prey", 414, 3, 8935),
    ];

    // 不在三张卡里、只影响团队频道列表的事件。017128D0 对 type 0x17（安徒恩，频道 19）与 0x19 走 017134ED：
    // (事件 0xB5=181 在 EVENT_INFO 集合 或 当前频道 type 0x11/0x15) 且任务 0x321D=12829 完成才保留，否则 2FE77B0 删除。
    // 181 在客户端的引用点与卢克 282 成对出现（0160477D/016046DF、036D74F1/036D7588、0399F38F/0399F3F1）。
    // 通道事件仍独立维护；选角次数投影另补类型0，供原生紧凑卡及可选第四张大卡读取。
    public readonly record struct ChannelEvent(string Name, ushort EventId, int Quest);

    public static readonly ChannelEvent[] ChannelEvents =
    [
        new("anton", 181, 12829),
    ];

    // 当前时刻处于 GM 时段内的卡与频道事件对应的客户端事件号；store 为空时全关。
    public static ushort[] OpenEvents(RaidEntryStore? store, DateTimeOffset instant) =>
        store is null ? [] :
        Cards.Where(card => store.IsOpen(card.Name, instant)).Select(card => card.EventId)
            .Concat(ChannelEvents.Where(channel => store.IsOpen(channel.Name, instant)).Select(channel => channel.EventId)).ToArray();
}
