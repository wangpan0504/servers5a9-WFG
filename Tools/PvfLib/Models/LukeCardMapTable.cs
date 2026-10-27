using System.Collections.Immutable;

namespace PvfLib;

// 042A6FF5..7048按三个整数的低字节保存；列含义尚未闭合，不命名为副本或迷宫索引。
public sealed record LukeCardMapRecord(byte RawA, byte RawB, byte RawC);

public static class LukeCardMapTable
{
    // 本解析器的保守资源预算，不表示原版路线数或随机范围。
    public const int MaximumRecords = 128;

    public static ImmutableArray<LukeCardMapRecord> Parse(IReadOnlyList<PvfToken> values)
    {
        if (values.Count == 0 || values.Count > MaximumRecords * 3 || values.Count % 3 != 0 ||
            values.Any(token => token.Type != 0 || token.Value is < 0 or > byte.MaxValue))
            throw new InvalidDataException("卢克卡牌地图声明必须为有界完整三字节记录，禁止截断整数");
        var rows = ImmutableArray.CreateBuilder<LukeCardMapRecord>(values.Count / 3);
        for (int i = 0; i < values.Count; i += 3)
            rows.Add(new((byte)values[i].Value, (byte)values[i + 1].Value, (byte)values[i + 2].Value));
        return rows.MoveToImmutable();
    }
}
