namespace DfoServer.Network.Builders;

// 次数形状只给测试对照这版 PVF：魔界裂缝 3，黎明裂缝 2。
// 原版读取器在地图容器指针为0时仍会做32次查找；配套95JP补丁保护空表并恢复黎明已用映射写入。
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

    // 023D6BF0读取总额度与32组(副本编号,已用次数)，块内前4字节保留。
    // 018EC077按总额度减已用次数，再以两者之差/总额度显示；映射值不能写成上限。
    public static byte[] Daybreak(uint total = DaybreakLimit, uint used = 0,
        uint dungeon = DaybreakDungeonId)
    {
        var body = new byte[8 + DaybreakTailBytes];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body, total);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), DaybreakTailBytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), dungeon);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), used);
        return body;
    }
}
