using DfoServer.Game.Pvp;

namespace DfoServer.Network.Builders;

internal static class PvpRecordPackets
{
    // 03BD3870：公共成功字节后固定33B；本人额外七个i32和分类行数，不能套用NOTI48或CMD499。
    internal static byte[] Score(PvpRecordSnapshot snapshot)
    {
        var stats = snapshot.Statistics;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(snapshot.CharacterId);
        writer.Write(snapshot.View);
        writer.Write(snapshot.Self ? (byte)1 : (byte)0);
        // 两个图标帧没有已确认的评级映射，保持原生构造中性帧，不能直接截断评分整数。
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write(stats.Wins);
        writer.Write(0); // 本服未开放的第二排位模式；不是总力或普通房间的胜数。
        writer.Write(stats.RecentWins);
        writer.Write(stats.RecentLosses);
        writer.Write(stats.RecentDraws);
        writer.Write(stats.WinStreak);
        writer.Write(0); // 当前赛季界面未使用的两个历史次数统计。
        writer.Write(0);
        writer.Write(stats.HighestWinStreak);
        if (snapshot.Self)
        {
            writer.Write(stats.Losses);
            writer.Write(stats.Draws);
            for (int field = 0; field < 5; field++) writer.Write(0);
            writer.Write((byte)0); // 分类键的语义未确认，不伪造分类统计。
        }
        return stream.ToArray();
    }
}
