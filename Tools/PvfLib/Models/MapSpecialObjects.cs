using System.Collections.Immutable;

namespace PvfLib;

public sealed record PvfMapSpecialChild(string Kind, ImmutableArray<int> Values);
public sealed record PvfMapSpecialObject(ImmutableArray<int> Values, ImmutableArray<PvfMapSpecialChild> Children);

public static class MapSpecialObjects
{
    public static ImmutableArray<PvfMapSpecialObject> Read(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var rows = ImmutableArray.CreateBuilder<PvfMapSpecialObject>();
        int at = 0;
        while (at < tokens.Count)
        {
            var header = Integers(5);
            if (header[4] < 0 || header[4] > (tokens.Count - at) / 2)
                throw new InvalidDataException("特殊被动对象的子数量越过资源块");
            var children = ImmutableArray.CreateBuilder<PvfMapSpecialChild>();
            for (int index = 0; index < header[4]; index++)
            {
                if (at >= tokens.Count || tokens[at].Type != 6) throw new InvalidDataException("特殊对象子类型不是字符串");
                string kind = resolve(tokens[at++].Value);
                ImmutableArray<int> values;
                if (kind == "[hellparty]")
                {
                    var triples = ImmutableArray.CreateBuilder<int>();
                    while (at < tokens.Count && tokens[at].Type == 0) triples.Add(tokens[at++].Value);
                    // ARAD 0426FFF0..04270063按group/rate/order三元组读到字符串闭合标签。
                    if (triples.Count % 3 != 0 || at >= tokens.Count || tokens[at].Type != 6 || resolve(tokens[at++].Value) != "[/hellparty]")
                        throw new InvalidDataException("特殊hellparty子块不成三元组或缺少闭合标签");
                    values = triples.ToImmutable();
                }
                else values = Integers(5);
                children.Add(new(kind, values));
            }
            rows.Add(new(header, children.ToImmutable()));
        }
        return rows.ToImmutable();

        ImmutableArray<int> Integers(int count)
        {
            if (tokens.Count - at < count) throw new InvalidDataException("特殊对象整数行被截断");
            var values = ImmutableArray.CreateBuilder<int>(count);
            for (int i = 0; i < count; i++)
            {
                if (tokens[at].Type != 0) throw new InvalidDataException("特殊对象整数行的token类型无效");
                values.Add(tokens[at++].Value);
            }
            return values.MoveToImmutable();
        }
    }
}
