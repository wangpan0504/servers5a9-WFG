using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal readonly record struct Strongway2019SymbolRequest(uint Side, uint Kind, uint RawValue);
internal readonly record struct Strongway2019MatchingControlRequest(byte Kind, ushort GameType)
{
    public bool IsMatching => Kind == 1 && GameType == 14;
}

internal static class Strongway2019Requests
{
    // 006C25A0仅生产该18B形状。语义拒绝返回false，由唯一520路由回复失败，不改PK的7B/18B合同。
    public static bool IsMatching(PvpMatchingRequest request) => request.Kind == 1 && request.GameType == 14 &&
        request.Parameters is [0, 0, 0] && request.Pairs.Length == 4 &&
        request.Pairs.All(pair => pair.First == byte.MaxValue && pair.Second == byte.MaxValue) && request.TrailingValue == 5360;

    // 006C2690的521及01FFBE60的522均写u8/u16；未知原值交业务拒绝，不改成首期常量。
    public static Strongway2019MatchingControlRequest MatchingControl(ReadOnlySpan<byte> body)
    {
        if (body.Length != 3) throw new InvalidDataException("首期匹配取消或退出正文长度错误");
        return new(body[0], BinaryPrimitives.ReadUInt16LittleEndian(body[1..]));
    }

    // 006C0EB0/006C1750共同写12B；0..4的RawValue未初始化，交领域忽略，不能按它选目标。
    public static Strongway2019SymbolRequest Symbol(ReadOnlySpan<byte> body)
    {
        if (body.Length != 12) throw new InvalidDataException("首期技能同步正文长度错误");
        return new(BinaryPrimitives.ReadUInt32LittleEndian(body), BinaryPrimitives.ReadUInt32LittleEndian(body[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[8..]));
    }
}
