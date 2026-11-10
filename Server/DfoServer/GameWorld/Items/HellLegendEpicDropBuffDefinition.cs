using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

// 通用效果表的星空单项计时视图；编号只保留协议身份和已核缺映射兼容。
internal sealed record HellLegendEpicDropBuffDefinition(int BuffId, int Param)
{
    public const int CanonicalBuffId = 1271;
    public const int CanonicalParam = 6520;
    public const int BonusScale = 100;
    public const int AbsentMappingItemId = 490011326;

    public string SystemGroup { get; init; } = "Dice_Attendance_5";
    public ImmutableHashSet<int>? ItemIds { get; init; }
    public bool IsFamilyItem(int itemId) => ItemIds?.Contains(itemId) == true;

    internal static ImmutableDictionary<int, HellLegendEpicDropBuffDefinition> Load(PvfArchive archive) => Load(VariousBuffCatalog.Load(archive));

    internal static bool IsLegacyView(VariousBuffDefinition buff) => buff.Timed && buff.SystemGroup == "Dice_Attendance_5" &&
        buff.DungeonScope == VariousBuffDungeonScope.HellLegend && buff.OnlyDropEffect(VariousBuffDropEffect.HellLegendEpic);

    internal static ImmutableDictionary<int, HellLegendEpicDropBuffDefinition> Load(VariousBuffCatalog catalog) => catalog.Items
        .Where(pair => pair.Value.Supported && IsLegacyView(pair.Value)).ToImmutableDictionary(pair => pair.Key,
            pair => new HellLegendEpicDropBuffDefinition(pair.Value.BuffId, pair.Value.Percent(VariousBuffDropEffect.HellLegendEpic))
            { SystemGroup = pair.Value.SystemGroup, ItemIds = catalog.Families[pair.Value.SystemGroup] });

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
                definition.BuffId == buff && definition.Param >= 0)
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
