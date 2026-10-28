namespace DfoServer.Network;

// 按整条连接观察序号，保留回卷前后的完整身份；重发和忙锁重试不能产生第二份购买。
internal sealed class ClientRequestSequence
{
    ulong high;
    bool initialized;

    public ulong Observe(ushort wire)
    {
        if (!initialized)
        {
            initialized = true;
            return high = 65536UL + wire;
        }
        int delta = (short)(wire - (ushort)high);
        if (delta > 0) high = checked(high + (uint)delta);
        return delta < 0 ? high - (uint)-delta : high;
    }
}
