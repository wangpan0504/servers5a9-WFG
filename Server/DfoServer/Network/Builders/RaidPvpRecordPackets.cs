using DfoServer.Game.Pvp;

namespace DfoServer.Network.Builders;

internal static class RaidPvpRecordPackets
{
    internal const uint Magic = 0x31505652; // RVP1，配套763桥严格识别，不猜原生空消费者正文。

    internal static byte[] Build(byte server, ushort target, byte channel, byte rawView, uint requestId,
        PvpRecordSnapshot? snapshot)
    {
        if (rawView is < 1 or > 5 || requestId == 0) throw new ArgumentOutOfRangeException(nameof(rawView));
        if (snapshot is not null && (snapshot.CharacterId != target || snapshot.Self || snapshot.View != rawView + 1))
            throw new ArgumentException("跨频道决斗记录必须是目标对应页面的公开快照", nameof(snapshot));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Magic);
        writer.Write(server);
        writer.Write(target);
        writer.Write(channel);
        writer.Write(rawView);
        writer.Write(requestId);
        writer.Write(snapshot is null ? (byte)0 : (byte)1);
        // 复用375公共记录33B，禁止本人统计尾部进入跨频道桥。
        if (snapshot is not null) writer.Write(PvpRecordPackets.Score(snapshot).AsSpan(1));
        return stream.ToArray();
    }
}
