using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

// 城镇移速只解释 village_move_speed + expire_time 的单映射；随机分支、空 buff、副本 one-match 不借此放行。
internal sealed record VillageMoveSpeedBuffDefinition(int BuffId, int Percent)
{
    public ImmutableHashSet<int>? ItemIds { get; init; }
    public bool IsFamilyItem(int itemId) => ItemIds?.Contains(itemId) == true;

    internal static ImmutableDictionary<int, VillageMoveSpeedBuffDefinition> Load(PvfArchive archive)
    {
        if (!archive.Contains("etc/variousbufflist.etc") || !archive.Contains("etc/variousbuffstackablelist.etc"))
            return ImmutableDictionary<int, VillageMoveSpeedBuffDefinition>.Empty;
        var rows = EpicDropBuffDefinition.ReadIndices(archive.ReadTokens("etc/variousbufflist.etc"), archive.ResolveString);
        var definitions = new Dictionary<int, VillageMoveSpeedBuffDefinition>();
        foreach (var group in rows.Where(row => row.Values is [{ Type: 0, Value: > 0 }]).GroupBy(row => row.Values[0].Value))
        {
            if (group.Count() != 1 || rows.Count(row => row.Values.Any(value => value.Type == 0 && value.Value == group.Key)) != 1)
                continue;
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
            if (effect is not { Values.Length: 0, Children.Length: 2 } || apply is not { Values.Length: 0, Children.Length: 2 } ||
                !Text(effect.Children, "name", "village_move_speed") ||
                Field(effect.Children, "param") is not { Children.Length: 0, Values: [{ Type: 0, Value: > 0 } percent] } ||
                !Text(apply.Children, "type", "expire_time") ||
                Field(apply.Children, "param") is not { Children.Length: 0, Values: [{ Type: 0, Value: > 0 }, { Type: 0 }] } ||
                !Numbers(row.Children, "overlap", 1) ||
                row.Children.Any(field => field.Name is not ("system group" or "icon index" or "title str" or "tooltip text" or
                    "tooltip duration" or "effect" or "overlap" or "overlap max" or "apply type" or "is save buff")))
                continue;
            definitions.Add(group.Key, new(group.Key, percent.Value));
        }
        return ParseMappings(EpicDropBuffDefinition.ReadIndices(archive.ReadTokens("etc/variousbuffstackablelist.etc"),
            archive.ResolveString), definitions);
    }

    internal static ImmutableDictionary<int, VillageMoveSpeedBuffDefinition> ParseMappings(
        ImmutableArray<PvfTokenSection> sections, IReadOnlyDictionary<int, VillageMoveSpeedBuffDefinition> definitions)
    {
        var result = ImmutableDictionary.CreateBuilder<int, VillageMoveSpeedBuffDefinition>();
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
            if (buff is { } id && definitions.TryGetValue(id, out var definition) && definition.BuffId == id && definition.Percent > 0)
                result.Add(group.Key, definition);
        }
        var ids = result.Keys.ToImmutableHashSet();
        foreach (var pair in result.ToArray()) result[pair.Key] = pair.Value with { ItemIds = ids };
        return result.ToImmutable();
    }
}
