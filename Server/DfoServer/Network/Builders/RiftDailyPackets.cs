namespace DfoServer.Network.Builders;

// 次数形状只给测试对照这版 PVF：魔界裂缝 3，黎明裂缝 2。
// PARTICULAR_HELL 与 CRACK_OF_DAYBREAK_INFO 在地图容器指针为 0 时仍会做 32 次查找并终止客户端。
// 本树没有 95JP 裂缝补丁，也不要在进城时发送这两个包。
internal static class RiftDailyPackets
{
    public const int ParticularHellLimit = 3;
    public const int DaybreakLimit = 2;
    public const int ParticularHellTailBytes = 0x104;

    public static byte[] ParticularHell()
    {
        var body = new byte[4 + ParticularHellTailBytes];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body, ParticularHellLimit);
        return body;
    }

    public static byte[] Daybreak()
    {
        var body = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body, DaybreakLimit);
        return body;
    }
}
