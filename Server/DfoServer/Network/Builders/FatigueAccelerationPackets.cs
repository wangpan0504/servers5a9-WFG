namespace DfoServer.Network.Builders;

internal static class FatigueAccelerationPackets
{
    // 本版490：保留u16后读取状态u8；0/1分别表示当前未开启/已开启。
    public static byte[] State(bool active) => [0, 0, active ? (byte)1 : (byte)0];
    public static byte[] Ack(ushort error) => error == 0 ? [1] : [0, (byte)error, (byte)(error >> 8)];
}
