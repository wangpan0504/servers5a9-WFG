using System.Collections.Immutable;

namespace DfoServer.Game.Dungeons;

// 进图伴侣由PVF地图的 [ai character] 行驱动，服务端只决定本次入场放行哪几行。
// 客户端换房间不销毁上一间房的AIC，逐房重发会每房多出一名并卡清房，因此只保留起始房间的行。
internal static class DungeonCompanion
{
    // 王富贵：priest/heal_soldier/heal_soldier.aic，20900 行写在每张可解析的副本地图里。
    public const int WangFuguiId = 20900;

    // 开关关闭时一行都不发；开启只放 20900。
    public static ImmutableArray<int> WangFuguiRows(bool summonWangFugui) =>
        summonWangFugui ? [WangFuguiId] : [];

    public static bool IsCompanionRow(int id) => id == WangFuguiId;
}
