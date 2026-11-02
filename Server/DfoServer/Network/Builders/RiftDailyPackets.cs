namespace DfoServer.Network.Builders;

// 次数形状只给测试对照这版 PVF：魔界裂缝 3，黎明裂缝 2。
// PARTICULAR_HELL 与 CRACK_OF_DAYBREAK_INFO 在地图容器指针为 0 时仍会做 32 次查找并终止客户端。
// 95JP 客户端支持 1545/1546；本服务仅在黎明裂缝区域或 530 结算上下文发送。
internal static class RiftDailyPackets
{
    public const int ParticularHellLimit = 3;
    public const int DaybreakLimit = 2;
    public const uint DaybreakDungeonId = 530;
    public const int ParticularHellTailBytes = 0x104;
    public const int DaybreakTailBytes = 0x104;

    public static byte[] ParticularHell()
    {
        var body = new byte[4 + ParticularHellTailBytes];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body, ParticularHellLimit);
        return body;
    }

    // 1545: u32 当前剩余、u32 后续块长度；块内前4字节为保留头，随后是32组(副本编号,上限)。
    // 读取器固定从块偏移4开始取映射；少了这4字节会把第一组映射错位，客户端只能显示当前值/当前值。
    public static byte[] Daybreak(uint remaining = DaybreakLimit, uint limit = DaybreakLimit,
        uint dungeon = DaybreakDungeonId)
    {
        var body = new byte[8 + DaybreakTailBytes];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body, remaining);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), DaybreakTailBytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), dungeon);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), limit);
        return body;
    }
}
