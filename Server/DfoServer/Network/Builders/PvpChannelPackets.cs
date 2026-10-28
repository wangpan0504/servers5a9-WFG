namespace DfoServer.Network.Builders;

internal static class PvpChannelPackets
{
    public static byte[] Unavailable() => [0, 0x15, 0];

    public static byte[] Available(IReadOnlyList<byte> channelNumbers)
    {
        if (channelNumbers.Count is 0 or > 255 || channelNumbers.Distinct().Count() != channelNumbers.Count ||
            channelNumbers.Contains((byte)0))
            throw new InvalidDataException("决斗入口必须是唯一的真实频道号");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        // 0234B340：首u32写角色+404，其完整语义尚未还原；本服入口兼容值为0。
        writer.Write(0u);
        writer.Write(checked((byte)channelNumbers.Count));
        foreach (byte number in channelNumbers)
        {
            writer.Write((uint)number);
            // 两个值分别进入频道map+10116C和+101180；本服兼容值0/1，不冒充原服算法。
            writer.Write(0u);
            writer.Write(1u);
        }
        return stream.ToArray();
    }
}
