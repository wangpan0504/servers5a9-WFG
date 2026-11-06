using System.Collections.Immutable;

namespace PvfLib;

// 本表只声明物品编号与原始整数参数；药剂种类、单位和消费业务不属于文件解析层。
public sealed record AlchemistPotionInfoFile(ImmutableDictionary<int, int> PotionInfo)
{
    public static AlchemistPotionInfoFile Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        var fields = document.Sections.Where(section => section.Name == "potion info").ToArray();
        if (fields.Length == 0) return new(ImmutableDictionary<int, int>.Empty);
        if (!document.Diagnostics.IsEmpty || fields is not [{ Children.Length: 0 } field] || field.Values.Length % 2 != 0)
            throw new InvalidDataException("炼金药剂参数表需要完整且唯一的两列声明");
        var result = ImmutableDictionary.CreateBuilder<int, int>();
        for (int at = 0; at < field.Values.Length; at += 2)
        {
            var item = field.Values[at];
            var parameter = field.Values[at + 1];
            if (item is not { Type: 0, Value: > 0 } || parameter.Type != 0 || !result.TryAdd(item.Value, parameter.Value))
                throw new InvalidDataException("炼金药剂参数表含无效编号、非整数或重复编号");
        }
        return new(result.ToImmutable());
    }
}
