using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal readonly record struct BossSelectionWeight(int MapId, int Weight);

// 按资源映射装配白斯追击次数效果；其他城镇药剂仍由各自玩法解释。
internal sealed record BossSelectionBuffDefinition(int BuffId, int DungeonId, int Entries,
    ImmutableArray<BossSelectionWeight> Weights)
{
    internal static ImmutableDictionary<int, BossSelectionBuffDefinition> Load(PvfArchive archive)
    {
        if (!archive.Contains("etc/variousbufflist.etc") || !archive.Contains("etc/variousbuffstackablelist.etc"))
            return ImmutableDictionary<int, BossSelectionBuffDefinition>.Empty;
        var buffs = new PvfTokenDocument(archive.ReadTokens("etc/variousbufflist.etc"), archive.ResolveString);
        PvfTokenSection? Field(ImmutableArray<PvfTokenSection> fields, string name) =>
            fields.Count(field => field.Name == name) == 1 ? fields.Single(field => field.Name == name) : null;
        bool Numbers(ImmutableArray<PvfTokenSection> fields, string name, params int[] expected) =>
            Field(fields, name) is { } field && field.Values.All(token => token.Type == 0) &&
            field.Values.Select(token => token.Value).SequenceEqual(expected);
        bool Text(ImmutableArray<PvfTokenSection> fields, string name, string expected) =>
            Field(fields, name) is { Values: [{ Type: 6 } value] } && archive.ResolveString(value.Value) == expected;
        var definitions = new Dictionary<int, BossSelectionBuffDefinition>();
        foreach (var (id, entries, weights) in new[]
        {
            (300, 5, new[] { 40, 40, 20 }), (1275, 5, new[] { 0, 0, 100 }), (1282, 1, new[] { 40, 40, 20 })
        })
        {
            var rows = buffs.Sections.Where(row => row.Name == "index" && row.Values is [{ Type: 0 } value] && value.Value == id).ToArray();
            if (rows is not [var buff]) continue;
            var effect = Field(buff.Children, "effect");
            var apply = Field(buff.Children, "apply type");
            var dungeon = Field(buff.Children, "apply dungeon type");
            if (effect is not { Children.Length: 2 } || apply is not { Children.Length: 2 } || dungeon is not { Values: [{ Type: 6 } kind, { Type: 0, Value: 7191 }] } ||
                archive.ResolveString(kind.Value) != "dugeon index" ||
                !Text(effect.Children, "name", "boss_selection_probability") ||
                !Numbers(effect.Children, "param", 7191, 92689, weights[0], 92690, weights[1], 92697, weights[2]) ||
                !Text(apply.Children, "type", "effect_apply") || !Numbers(apply.Children, "param", entries, 0) ||
                !Numbers(buff.Children, "is save buff", 1) || !Numbers(buff.Children, "is delete daily", 1) ||
                !Numbers(buff.Children, "party member count", 1, 1) || !Numbers(buff.Children, "overlap", 1) || !Numbers(buff.Children, "overlap max", 1) ||
                buff.Children.Any(field => field.Name is not ("system group" or "title str" or "tooltip text" or "icon index" or
                    "tooltip duration" or "effect" or "overlap" or "overlap max" or "apply type" or "is save buff" or
                    "is delete daily" or "apply dungeon type" or "party member count")))
                continue;
            definitions.Add(id, new(id, 7191, entries,
                [new(92689, weights[0]), new(92690, weights[1]), new(92697, weights[2])]));
        }
        var mapping = new PvfTokenDocument(archive.ReadTokens("etc/variousbuffstackablelist.etc"), archive.ResolveString);
        return mapping.Sections.Where(row => row.Name == "index" && row.Values is [{ Type: 0, Value: > 0 }])
            .GroupBy(row => row.Values[0].Value).Where(group => group.Count() == 1 &&
                group.Single().Children is [{ Name: "buff list", Values: [{ Type: 0 } value] }] && definitions.ContainsKey(value.Value))
            .ToImmutableDictionary(group => group.Key, group => definitions[group.Single().Children[0].Values[0].Value]);
    }
}
