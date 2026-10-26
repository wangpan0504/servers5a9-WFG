namespace DfoServer.Network.Builders.Inventory;

internal static class AvatarAbilityChangePackets
{
    public static byte[] Success(short slot, byte ability) => [1, (byte)slot, (byte)(slot >> 8), ability];
    // 027960B0接受公共失败头；具体业务文案编号未确认，使用通用0而不借用其它道具错误表。
    public static byte[] Failure() => [0, 0, 0];
}
