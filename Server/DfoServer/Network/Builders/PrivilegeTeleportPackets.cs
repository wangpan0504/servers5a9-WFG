namespace DfoServer.Network.Builders;

internal static class PrivilegeTeleportPackets
{
    // 00978D00只检查公共u16错误，并消费但不使用后5字节；占位置零是本服序列化选择。
    // 成功后由客户端资源5秒启动相对冷却，不能把占位解释为截止时间或另造冷却NOTI。
    public static byte[] Committed() => [1, 0, 0, 0, 0, 0];
    public static byte[] Rejected(bool badArea) => [0, badArea ? (byte)2 : (byte)1, 0, 0, 0, 0, 0, 0];
}
