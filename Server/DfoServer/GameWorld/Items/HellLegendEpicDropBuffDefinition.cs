using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

// 星空裂缝史诗掉率只解释已核定的 1271 合同；固定 1136 与随机倍率不借此放行。
internal sealed record HellLegendEpicDropBuffDefinition(int BuffId, int Param)
{
    public const int CanonicalBuffId = 1271;
    public const int CanonicalParam = 6520;
    public const int BonusScale = 10000;
    public const int AbsentMappingItemId = 490011326;

    public ImmutableHashSet<int>? ItemIds { get; init; }
    public bool IsFamilyItem(int itemId) => ItemIds?.Contains(itemId) == true;

    internal static ImmutableDictionary<int, HellLegendEpicDropBuffDefinition> Load(PvfArchive archive)
    {
        if (!archive.Contains("etc/variousbufflist.etc") || !archive.Contains("etc/variousbuffstackablelist.etc"))
            return ImmutableDictionary<int, HellLegendEpicDropBuffDefinition>.Empty;
        var mappings = EpicDropBuffDefinition.ReadIndices(archive.ReadTokens("etc/variousbuffstackablelist.etc"),
            archive.ResolveString);
        var rows = EpicDropBuffDefinition.ReadIndices(archive.ReadTokens("etc/variousbufflist.etc"), archive.ResolveString)
            .Where(row => row.Values is [{ Type: 0, Value: CanonicalBuffId }]).ToArray();
        if (rows is not [var buff])
            return ParseMappings(mappings, ImmutableDictionary<int, HellLegendEpicDropBuffDefinition>.Empty);
        PvfTokenSection? Field(ImmutableArray<PvfTokenSection> fields, string name) =>
            fields.Count(field => field.Name == name) == 1 ? fields.Single(field => field.Name == name) : null;
        bool Text(ImmutableArray<PvfTokenSection> fields, string name, string expected) =>
            Field(fields, name) is { Children.Length: 0, Values: [{ Type: 6 } value] } && archive.ResolveString(value.Value) == expected;
        bool Numbers(ImmutableArray<PvfTokenSection> fields, string name, params int[] expected) =>
            Field(fields, name) is { Children.Length: 0 } field && field.Values.All(value => value.Type == 0) &&
            field.Values.Select(value => value.Value).SequenceEqual(expected);
        var effect = Field(buff.Children, "effect");
        var apply = Field(buff.Children, "apply type");
        var definitions = new Dictionary<int, HellLegendEpicDropBuffDefinition>();
        if (Text(buff.Children, "system group", "Dice_Attendance_5") &&
            effect is { Values.Length: 0, Children.Length: 2 } && apply is { Values.Length: 0, Children.Length: 2 } &&
            Text(effect.Children, "name", "hell_legend_dungeon_epic_drop_rate_up") &&
            Field(effect.Children, "param") is { Children.Length: 0, Values: [{ Type: 0, Value: CanonicalParam }] } &&
            Text(apply.Children, "type", "expire_time") && Numbers(apply.Children, "param", 300, 0) &&
            Text(buff.Children, "apply dungeon type", "only hell legend seal dungeon") &&
            Numbers(buff.Children, "is save buff", 1) &&
            buff.Children.All(field => field.Name is "system group" or "icon index" or "title str" or "tooltip text" or
                "tooltip text mode" or "effect" or "apply type" or "apply dungeon type" or "is save buff"))
            definitions.Add(CanonicalBuffId, new(CanonicalBuffId, CanonicalParam));
        return ParseMappings(mappings, definitions);
    }

    internal static ImmutableDictionary<int, HellLegendEpicDropBuffDefinition> ParseMappings(
        ImmutableArray<PvfTokenSection> sections, IReadOnlyDictionary<int, HellLegendEpicDropBuffDefinition> definitions)
    {
        var declared = sections.Where(row => row.Name == "index").SelectMany(row => row.Values)
            .Where(value => value.Type == 0).Select(value => value.Value).ToHashSet();
        var result = ImmutableDictionary.CreateBuilder<int, HellLegendEpicDropBuffDefinition>();
        foreach (var group in sections.Where(row => row.Name == "index" && row.Values is [{ Type: 0, Value: > 0 }])
            .GroupBy(row => row.Values[0].Value))
        {
            var copies = group.ToArray();
            if (sections.Count(row => row.Name == "index" && row.Values.Any(value => value.Type == 0 && value.Value == group.Key)) !=
                copies.Length)
                continue;
            int? mapped = null;
            foreach (var copy in copies)
            {
                if (copy.Children is not [{ Name: "buff list", Children.Length: 0, Values: [{ Type: 0 } value] }])
                {
                    mapped = null;
                    break;
                }
                if (mapped is { } existing && existing != value.Value)
                {
                    mapped = null;
                    break;
                }
                mapped = value.Value;
            }
            if (mapped is { } buff && definitions.TryGetValue(buff, out var definition) &&
                definition.BuffId == buff && definition.Param == CanonicalParam)
                result.Add(group.Key, definition);
        }
        // 本服仅补这一个已核缺映射物品；显式但损坏/重复冲突/其它效果的声明仍拒绝。
        if (!declared.Contains(AbsentMappingItemId) &&
            definitions.TryGetValue(CanonicalBuffId, out var canonical) &&
            canonical is { BuffId: CanonicalBuffId, Param: CanonicalParam })
            result.Add(AbsentMappingItemId, canonical);
        var ids = result.Keys.ToImmutableHashSet();
        foreach (var pair in result.ToArray()) result[pair.Key] = pair.Value with { ItemIds = ids };
        return result.ToImmutable();
    }
}
