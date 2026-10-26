using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal readonly record struct BossSelectionWeight(int MapId, int Weight);

// 仅接入截图药水对应的300号次数效果；其他城镇药剂仍须由各自玩法解释。
internal sealed record BossSelectionBuffDefinition(int BuffId, int DungeonId, int Entries,
    ImmutableArray<BossSelectionWeight> Weights)
{
    internal static (BossSelectionBuffDefinition? Definition, ImmutableHashSet<int> Items) Load(PvfArchive archive)
    {
        if (!archive.Contains("etc/variousbufflist.etc") || !archive.Contains("etc/variousbuffstackablelist.etc"))
            return (null, []);
        var buffs = new PvfTokenDocument(archive.ReadTokens("etc/variousbufflist.etc"), archive.ResolveString);
        var rows = buffs.Sections.Where(row => row.Name == "index" && row.Values is [{ Type: 0, Value: 300 }]).ToArray();
        if (rows is not [var buff]) return (null, []);
        PvfTokenSection? Field(ImmutableArray<PvfTokenSection> fields, string name) =>
            fields.Count(field => field.Name == name) == 1 ? fields.Single(field => field.Name == name) : null;
        bool Numbers(ImmutableArray<PvfTokenSection> fields, string name, params int[] expected) =>
            Field(fields, name) is { } field && field.Values.All(token => token.Type == 0) &&
            field.Values.Select(token => token.Value).SequenceEqual(expected);
        bool Text(ImmutableArray<PvfTokenSection> fields, string name, string expected) =>
            Field(fields, name) is { Values: [{ Type: 6 } value] } && archive.ResolveString(value.Value) == expected;
        var effect = Field(buff.Children, "effect");
        var apply = Field(buff.Children, "apply type");
        var dungeon = Field(buff.Children, "apply dungeon type");
        if (effect is not { Children.Length: 2 } || apply is not { Children.Length: 2 } || dungeon is not { Values: [{ Type: 6 } kind, { Type: 0, Value: 7191 }] } ||
            archive.ResolveString(kind.Value) != "dugeon index" ||
            !Text(effect.Children, "name", "boss_selection_probability") ||
            !Numbers(effect.Children, "param", 7191, 92689, 40, 92690, 40, 92697, 20) ||
            !Text(apply.Children, "type", "effect_apply") || !Numbers(apply.Children, "param", 5, 0) ||
            !Numbers(buff.Children, "is save buff", 1) || !Numbers(buff.Children, "is delete daily", 1) ||
            !Numbers(buff.Children, "party member count", 1, 1) || !Numbers(buff.Children, "overlap", 1) || !Numbers(buff.Children, "overlap max", 1) ||
            buff.Children.Any(field => field.Name is not ("system group" or "title str" or "tooltip text" or "icon index" or
                "tooltip duration" or "effect" or "overlap" or "overlap max" or "apply type" or "is save buff" or
                "is delete daily" or "apply dungeon type" or "party member count")))
            return (null, []);
        var mapping = new PvfTokenDocument(archive.ReadTokens("etc/variousbuffstackablelist.etc"), archive.ResolveString);
        var items = mapping.Sections.Where(row => row.Name == "index" && row.Values is [{ Type: 0, Value: > 0 }])
            .GroupBy(row => row.Values[0].Value).Where(group => group.Count() == 1 &&
                group.Single().Children is [{ Name: "buff list", Values: [{ Type: 0, Value: 300 }] }])
            .Select(group => group.Key).ToImmutableHashSet();
        return (new(300, 7191, 5, [new(92689, 40), new(92690, 40), new(92697, 20)]), items);
    }
}
