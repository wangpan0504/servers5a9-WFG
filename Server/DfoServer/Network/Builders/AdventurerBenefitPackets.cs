namespace DfoServer.Network.Builders;

internal static class AdventurerBenefitPackets
{
    // NOTI1761由00498680固定读取16B；00498290区分1=回归、2=新手，00498160读取+12的绝对期限。
    // +4是首次提示已展示标记（读者弹窗后自行置1），不是领取成功。GM主动设身份不重弹首次引导。
    public static byte[] Returning(long until)
    {
        if (until is < 0 or > uint.MaxValue) throw new InvalidDataException("回归福利期限超出原生协议范围");
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write(until == 0 ? 0u : 1u);
        writer.Write(until == 0 ? (ushort)0 : (ushort)1);
        // 尚未领取任何奖励；保留原生初始状态，不伪造欢迎、每日或升级奖励的已领取事实。
        writer.Write((ushort)0); writer.Write(0u); writer.Write((uint)until);
        return bytes.ToArray();
    }
}
