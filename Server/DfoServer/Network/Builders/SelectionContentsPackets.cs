using DfoServer.Game.Characters;

namespace DfoServer.Network.Builders;

internal static class SelectionContentsPackets
{
    static byte Count(int value) => checked((byte)Math.Clamp(value, 0, byte.MaxValue));

    // 023A0AC0 → 03B42740：绝望剩余/上限、悲叹剩余/上限；018EC532也复用地图账号提示。
    public static byte[] Towers(SelectionTowerCounts counts) =>
        [Count(counts.DespairRemaining), Count(counts.DespairMaximum), Count(counts.GriefRemaining), Count(counts.GriefMaximum)];

    // 0233C450：ACK成功字节由dispatcher先读；type=0为完整数据，type=1会清掉塔与异界字段。
    public static byte[] Character(byte index, SelectionContents contents)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(index);
        writer.Write((byte)0);
        writer.Write(contents.DespairUsed);
        writer.Write(contents.DespairFloor);
        writer.Write(contents.DimensionUsed);
        writer.Write(0); // 当前选角渲染未读取的第四个i32，保留协议位置。
        writer.Write(contents.GriefUsed);
        writer.Write(contents.GriefFloor);
        writer.Write((byte)4);
        for (byte kind = 0; kind < 4; kind++) { writer.Write(kind); writer.Write(Count(contents.RaidUsed[kind])); }
        writer.Write(Count(contents.TayberrsRemaining));
        writer.Write(Count(contents.NormalFiendRemaining));
        writer.Write(Count(contents.DazzlementUsed));
        writer.Write(Count(contents.HopeRemaining));
        writer.Write((ushort)Count(contents.RaidUsed[0])); // 旧安徒恩位置；后续字节覆盖高位。
        writer.Write(Count(contents.WarRemaining));
        writer.Write(Count(contents.GuildRemaining));
        return stream.ToArray();
    }
}
