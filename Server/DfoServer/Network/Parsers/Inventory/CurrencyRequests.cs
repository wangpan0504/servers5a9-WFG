namespace DfoServer.Network.Parsers.Inventory;

internal static class CurrencyRequests
{
    public static bool IsCeraQuery(ReadOnlySpan<byte> body) => body.IsEmpty;
    public static byte GoldLimitUpgrade(ReadOnlySpan<byte> body)
    {
        // 0210EDD0: 从 ebp-0x14 发送14B，offset13 是请求的下一阶段，其余字段不作为扣费依据。
        if (body.Length != 14) throw new InvalidDataException("金币携带上限请求宽度错误");
        return body[13];
    }
}
