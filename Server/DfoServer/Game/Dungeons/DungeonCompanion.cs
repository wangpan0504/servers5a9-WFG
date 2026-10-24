using System.Collections.Immutable;

namespace DfoServer.Game.Dungeons;

// 进图伴侣由PVF地图的 [ai character] 行驱动，服务端只决定本次入场放行哪几行。
// 客户端换房间不销毁上一间房的AIC，逐房重发会每房多出一名并卡清房，因此只保留起始房间的行。
internal static class DungeonCompanion
{
    // 王富贵：priest/heal_soldier/heal_soldier.aic，20900 行写在每张可解析的副本地图里。
    public const int WangFuguiId = 20900;
    // 光兵：沿用28号本体 fighter/28/28.aic（人偶 2600261 用的同一编号），5409 行写在每张可解析的副本地图里。
    // 20903/20904 在 aicharacter.lst 里没有注册，客户端建不出来会闪退，不能再用那两个编号。
    public const int GuangbingId = 5409;

    // 两个开关各自独立、可同时开启；关闭的开关一行都不发，行序仍按地图 [ai character] 原顺序。
    public static ImmutableArray<int> EnabledRows(bool summonWangFugui, bool summonGuangbing)
    {
        var rows = ImmutableArray.CreateBuilder<int>(2);
        if (summonWangFugui) rows.Add(WangFuguiId);
        if (summonGuangbing) rows.Add(GuangbingId);
        return rows.ToImmutable();
    }

    // 两行都算伴侣：未放行时在房间计划里丢弃，放行后按资源行原样发送。
    public static bool IsCompanionRow(int id) => id is WangFuguiId or GuangbingId;
}
