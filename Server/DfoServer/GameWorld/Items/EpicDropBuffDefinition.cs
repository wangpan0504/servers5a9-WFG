using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

// 固定史诗掉率效果仅解释已核定的资源合同；概率选择和星空效果不借此放行。
internal sealed record EpicDropBuffDefinition(int BuffId, int Percent)
{
    public ImmutableHashSet<int>? ItemIds { get; init; }
    public bool IsFamilyItem(int itemId) => ItemIds?.Contains(itemId) == true;

    internal static ImmutableDictionary<int, EpicDropBuffDefinition> Load(PvfArchive archive)
    {
        if (!archive.Contains("etc/variousbufflist.etc") || !archive.Contains("etc/variousbuffstackablelist.etc"))
            return ImmutableDictionary<int, EpicDropBuffDefinition>.Empty;
        var rows = ReadIndices(archive.ReadTokens("etc/variousbufflist.etc"), archive.ResolveString);
        var definitions = new Dictionary<int, EpicDropBuffDefinition>();
        foreach (var group in rows.Where(row => row.Values is [{ Type: 0, Value: > 0 }]).GroupBy(row => row.Values[0].Value))
        {
            if (group.Count() != 1 || rows.Count(row => row.Values.Any(value => value.Type == 0 && value.Value == group.Key)) != 1) continue;
            var row = group.Single();
            PvfTokenSection? Field(ImmutableArray<PvfTokenSection> fields, string name) =>
                fields.Count(field => field.Name == name) == 1 ? fields.Single(field => field.Name == name) : null;
            bool Text(ImmutableArray<PvfTokenSection> fields, string name, string expected) =>
                Field(fields, name) is { Children.Length: 0, Values: [{ Type: 6 } value] } && archive.ResolveString(value.Value) == expected;
            bool Numbers(ImmutableArray<PvfTokenSection> fields, string name, params int[] expected) =>
                Field(fields, name) is { Children.Length: 0 } field && field.Values.All(value => value.Type == 0) &&
                field.Values.Select(value => value.Value).SequenceEqual(expected);
            var effect = Field(row.Children, "effect");
            var apply = Field(row.Children, "apply type");
            if (!Text(row.Children, "system group", "random_box_buff") ||
                effect is not { Values.Length: 0, Children.Length: 2 } || apply is not { Values.Length: 0, Children.Length: 2 } ||
                !Text(effect.Children, "name", "epic_rarity_item_drop_rate") ||
                Field(effect.Children, "param") is not { Children.Length: 0, Values: [{ Type: 0, Value: 100 } percent] } ||
                !Text(apply.Children, "type", "expire_time") || !Numbers(apply.Children, "param", 0, 0) ||
                !Text(row.Children, "apply dungeon type", "all") || !Numbers(row.Children, "overlap", 1) ||
                row.Children.Any(field => field.Name is not ("system group" or "icon index" or "title str" or
                    "tooltip text" or "effect" or "overlap" or "apply type" or "apply dungeon type"))) continue;
            definitions.Add(group.Key, new(group.Key, percent.Value));
        }
        return ParseMappings(ReadIndices(archive.ReadTokens("etc/variousbuffstackablelist.etc"), archive.ResolveString), definitions);
    }

    // 单独切出每个index再解析，损坏的未闭合块不能把下一条合法声明吞作自己的子字段。
    internal static ImmutableArray<PvfTokenSection> ReadIndices(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var result = ImmutableArray.CreateBuilder<PvfTokenSection>();
        for (int at = 0; at < tokens.Count;)
        {
            if (tokens[at].Type != 3 || resolve(tokens[at].Value) != "[index]") { at++; continue; }
            int start = at++;
            int values = at;
            while (at < tokens.Count && tokens[at].Type != 3) at++;
            var identity = tokens.Skip(values).Take(at - values).ToImmutableArray();
            while (at < tokens.Count && !(tokens[at].Type == 3 && resolve(tokens[at].Value) == "[index]")) at++;
            // 正式表这两项只漏最外层闭合；只接受完整且唯一的六token固定1136/100声明。
            // 不修复未知条目，也不借下一项的结束标签补齐当前项。
            if (at - start == 6 && identity is [{ Type: 0, Value: 490009005 or 490703161 }] &&
                tokens[start + 2].Type == 3 && resolve(tokens[start + 2].Value) == "[probability buff list]" &&
                tokens[start + 3] is { Type: 0, Value: 1136 } && tokens[start + 4] is { Type: 0, Value: 100 } &&
                tokens[start + 5].Type == 3 && resolve(tokens[start + 5].Value) == "[/probability buff list]")
            {
                result.Add(new("index", identity,
                    [new("probability buff list", [tokens[start + 3], tokens[start + 4]], [], true)], true));
                continue;
            }
            try
            {
                var document = new PvfTokenDocument(tokens.Skip(start).Take(at - start).ToArray(), resolve);
                if (document.Diagnostics.IsEmpty && document.Sections is [{ Name: "index", IsBlock: true } row])
                { result.Add(row); continue; }
            }
            catch (InvalidDataException) { }
            // 保留无效声明的身份，缺映射兼容不得覆盖任何原声明。
            result.Add(new("index", identity, []));
        }
        return result.ToImmutable();
    }

    internal static ImmutableDictionary<int, EpicDropBuffDefinition> ParseMappings(ImmutableArray<PvfTokenSection> sections,
        IReadOnlyDictionary<int, EpicDropBuffDefinition> definitions)
    {
        var declared = sections.Where(row => row.Name == "index").SelectMany(row => row.Values)
            .Where(value => value.Type == 0).Select(value => value.Value).ToHashSet();
        var result = ImmutableDictionary.CreateBuilder<int, EpicDropBuffDefinition>();
        foreach (var group in sections.Where(row => row.Name == "index" && row.Values is [{ Type: 0, Value: > 0 }])
            .GroupBy(row => row.Values[0].Value))
        {
            if (group.Count() != 1 || sections.Count(row => row.Name == "index" &&
                    row.Values.Any(value => value.Type == 0 && value.Value == group.Key)) != 1 ||
                group.Single().Children is not [{ Children.Length: 0 } field]) continue;
            int? buff = field switch
            {
                { Name: "buff list", Values: [{ Type: 0 } value] } => value.Value,
                { Name: "probability buff list", Values: [{ Type: 0 } value, { Type: 0, Value: 100 }] } => value.Value,
                _ => null
            };
            if (buff is { } id && definitions.TryGetValue(id, out var definition) && definition.BuffId == id && definition.Percent == 100)
                result.Add(group.Key, definition);
        }
        // 本服仅补这一个已核缺映射物品；显式但损坏/重复/随机的声明仍拒绝。
        if (!declared.Contains(490000413) && definitions.TryGetValue(1136, out var canonical) && canonical is { BuffId: 1136, Percent: 100 })
            result.Add(490000413, canonical);
        var ids = result.Keys.ToImmutableHashSet();
        foreach (var pair in result.ToArray()) result[pair.Key] = pair.Value with { ItemIds = ids };
        return result.ToImmutable();
    }
}
