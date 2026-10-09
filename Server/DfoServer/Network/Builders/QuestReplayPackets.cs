namespace DfoServer.Network.Builders;

internal static class QuestReplayPackets
{
    // 02348B00成功分支读取后丢弃单u32，随后使用已有本地回放管理器启动15/16。
    public static byte[] Accepted() => [1,0,0,0,0];
    public static byte[] Rejected() => [0,19,0];
}
