namespace DfoServer.Network.Builders.Inventory;

// 待实机验证：FUN_0236eba0（0x345）与 FUN_0236ed20（0x362）在成功分支读一个 u32 产物编号，
// 失败分支按 u16 错误码显示消息（0x3c/0x5f 有专属文案）。本版按该读取宽度组包。
internal static class GuildCompoundPackets
{
    public static byte[] Success(int productItemId)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(productItemId);
        return stream.ToArray();
    }

    public static byte[] Failure(ushort code)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)0);
        writer.Write(code);
        return stream.ToArray();
    }
}
