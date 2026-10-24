using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using PvfLib;

namespace DfoServer;

// #613 特殊区域页频道的落点、快照谓词与 ACK LOGIN 类型。数据读匹配的 Script.pvf，非实机。
internal static class SpecialAreaChannelTests
{
    public static void Run(TownCatalog towns, PvfArchive pvf)
    {
        int before = Count;
        var none = new HashSet<int>();
        var elven = new ChannelEntry(new(2, 1), 22, "[elven_guard]");
        var evildom = new ChannelEntry(new(2, 41), ChannelAreaLanding.EvildomWarType, "[evildom_war]");
        var trade = new ChannelEntry(new(2, 32), 3, "[trade]");
        That(ChannelAreaLanding.Decide(towns, trade, 95, none) is { Spot: null, Reason: "no-proven-town" },
            "交易-拍卖行没有专属城镇，选角按普通规则落点");

        // 五陵：CHANNEL_XIAN_VILLAGE 0x38，town 117 chn_Teaching_xian.twn。
        var xian = new ChannelEntry(new(2, 43), ChannelAreaLanding.XianVillageType, "[xian_village]");
        That(ChannelAreaLanding.XianVillageType == 0x38, "CHANNEL_XIAN_VILLAGE = 0x38");
        That(System.Text.RegularExpressions.Regex.IsMatch(pvf.ReadText("town/town.lst"), @"\b117\s+`chn_Teaching_xian\.twn`"),
            "town.lst 117 = chn_Teaching_xian.twn");
        That(pvf.ReadText("town/chn_teaching_xian.twn").Contains("`五陵`"), "chn_Teaching_xian.twn [name] 五陵");
        var gate = towns.Areas[(117, 0)].Gate;
        That(gate == new Location(117, 0, 474, 234), "117/0 [gate] 474 234: " + gate);
        foreach (int level in new[] { 1, 95 })
        {
            var landing = ChannelAreaLanding.Decide(towns, xian, level, none);
            That(landing.Spot == gate && landing.Reason == "area-gate" && towns.IsPosition(landing.Spot!, none),
                $"Lv{level} 五陵频道落到 117/0 门口: {landing.Spot} {landing.Reason}");
        }
        That(!ChannelAreaLanding.IsChannelBoundTown(towns, 117), "五陵 117 有其他城镇连入，不是频道专属城镇");
        That(ChannelAreaLanding.ClientKeepsTownSnapshot(towns, xian, new(38, 0, 1, 1)) &&
            ChannelAreaLanding.ClientKeepsTownSnapshot(towns, elven, gate!) &&
            !ChannelAreaLanding.ClientKeepsTownSnapshot(towns, xian, new(67, 0, 1, 1)) &&
            !ChannelAreaLanding.ClientKeepsTownSnapshot(towns, evildom, gate!),
            "0x38 与普通谓词全同互换保留快照；与魔界大战 0x36 互换不保留");
        That(ChannelPackets.LoginChannelType(xian) == 0 && ChannelPackets.LoginChannelType(trade) == 0,
            "五陵/交易频道 ACK LOGIN 第 3 字节保持 0（未证客户端读取）");

        // 勇士对决：CHANNEL_DNFAOS_FREE 0x3F / CHANNEL_DNFAOS_RANK 0x3E，town 119 dnfaos.twn，区域 0 地图缺失取 119/1。
        var free = new ChannelEntry(new(2, 44), ChannelAreaLanding.DnfaosFreeType, "[dnfaos_free]");
        var rank = new ChannelEntry(new(2, 45), ChannelAreaLanding.DnfaosRankType, "[dnfaos_rank]");
        var fiendwar = new ChannelEntry(new(2, 39), ChannelAreaLanding.FiendwarType, "[fiendwar]");
        That(free.Type == 0x3F && rank.Type == 0x3E, "CHANNEL_DNFAOS_FREE = 0x3F, CHANNEL_DNFAOS_RANK = 0x3E");
        That(System.Text.RegularExpressions.Regex.IsMatch(pvf.ReadText("town/town.lst"), @"\b119\s+`dnfaos\.twn`"), "town.lst 119 = dnfaos.twn");
        foreach (var entry in new[] { free, rank })
        {
            var landing = ChannelAreaLanding.Decide(towns, entry, 95, none);
            That(landing.Spot == new Location(119, 1, 270, 148) && towns.IsPosition(landing.Spot!, none),
                $"Lv95 {entry.Group} 落到 119/1 邻门 270,148: {landing.Spot} {landing.Reason}");
        }
        var arena = new Location(119, 1, 270, 148);
        That(ChannelAreaLanding.IsChannelBoundTown(towns, 119), "119 混沌之村暴风雨是频道专属城镇");
        That(ChannelAreaLanding.ClientKeepsTownSnapshot(towns, rank, arena) &&
            ChannelAreaLanding.ClientKeepsTownSnapshot(towns, fiendwar, arena) &&
            !ChannelAreaLanding.ClientKeepsTownSnapshot(towns, elven, arena) &&
            !ChannelAreaLanding.ClientKeepsTownSnapshot(towns, evildom, arena) &&
            !ChannelAreaLanding.ClientKeepsTownSnapshot(towns, free, new(38, 0, 1, 1)),
            "勇士对决与团队页签类型互换保留快照；与普通/魔界大战互换不保留");
        That(ChannelPackets.LoginChannelType(free) == 0 && ChannelPackets.LoginChannelType(rank) == 0,
            "勇士对决 ACK LOGIN 第 3 字节保持 0（未证客户端读取）");
        Console.WriteLine($"PASS {Count - before} 特殊区域页频道断言（非实机）");
    }
}
