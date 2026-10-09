using System.Collections.Immutable;

namespace PvfLib;

// MAP中的APC行与AIC最低信息保留本版原始排列，不解释阵营、品级或奖励。
public sealed record PvfAiCharacterSpawn(ImmutableArray<int> Values, string Faction, string Rank);

public static class PvfAiCharacterScript
{
    public static ImmutableArray<PvfAiCharacterSpawn> MapRows(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        if (tokens.Count % 8 != 0)
            throw new InvalidDataException("地图ai character不是完整八token行");
        var rows = ImmutableArray.CreateBuilder<PvfAiCharacterSpawn>();
        for (int at = 0; at < tokens.Count; at += 8)
        {
            if (Enumerable.Range(0,4).Concat([6,7]).Any(offset => tokens[at + offset].Type != 0) ||
                tokens[at + 4].Type != 6 || tokens[at + 5].Type != 6)
                throw new InvalidDataException("地图ai character整数或字符串排列错误");
            rows.Add(new([.. Enumerable.Range(0,4).Concat([6,7]).Select(offset => tokens[at + offset].Value)],
                resolve(tokens[at + 4].Value),resolve(tokens[at + 5].Value)));
        }
        return rows.ToImmutable();
    }

    public static ImmutableArray<PvfToken> MinimumInfo(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        ImmutableArray<PvfToken>? found = null;
        for (int at = 0; at < tokens.Count; at++)
        {
            if (tokens[at].Type != 3 || !resolve(tokens[at].Value).Equals("[minimum info]",StringComparison.OrdinalIgnoreCase)) continue;
            if (found is not null) throw new InvalidDataException("AIC最低信息重复");
            int start = ++at;
            while (at < tokens.Count && tokens[at].Type != 3) at++;
            found = [.. tokens.Skip(start).Take(at - start)];
            at--;
        }
        return found ?? throw new InvalidDataException("AIC缺少最低信息");
    }
}
