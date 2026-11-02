namespace DfoServer.Game.BattlePass;

internal static class BattlePassCeraConsumption
{
    // 本服累计分子余数政策，跨订单保留0..99；原资源仅提供百分比，不声称复原隐藏舍入。
    public static (long Experience, int Remainder) Convert(long cera, int percentage, int remainder)
    {
        if (cera < 0 || percentage is < 0 or > ushort.MaxValue || remainder is < 0 or > 99)
            throw new InvalidDataException("战令消费经验参数无效");
        // 先分解，避免合法long点券在乘百分比时中间溢出；最终XP仍checked。
        long whole = checked(cera / 100 * percentage);
        long fraction = checked(cera % 100 * percentage + remainder);
        return (checked(whole + fraction / 100), checked((int)(fraction % 100)));
    }
}
