namespace DfoServer.Network.Builders;

internal static class DungeonPackets
{
    // ACK15 成功标志为 1 时，02348EFD 不再读后续字段；这里只用于已有选图界面路径。
    public static byte[] SelectionOpened() => [1];

    public static byte[] SelectionUnavailable() => [0, 1, 0];

    // NOTI27 在 023D8AC0 的空列表足迹：计数字段为 0 时不读列表项，共 42 字节。
    // 顺序是 u32、u32、u8、u32、u8、u8、u8、u32、u16、u16、u32、u8、u8、u32、u32、u8、u16、u8。
    public static byte[] EmptySelectNotice() => new byte[42];
}
