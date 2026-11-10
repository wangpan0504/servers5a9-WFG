using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal enum DungeonEpicDropScope { Ordinary, Inferno, Particular, Daybreak, HellLegend }

// 通用掉落效果的组合视图；每个effect按name解释，Buff编号不决定业务。
internal sealed record DungeonEpicDropBuffDefinition(int BuffId, ImmutableDictionary<DungeonEpicDropScope, int> Rates,
    int BossDropPercent = 0)
{
    public string SystemGroup { get; init; } = "Dice_Attendance_5";
    public VariousBuffDefinition? Resource { get; init; }
    public bool Timed => Resource?.Timed == true;
    public ImmutableHashSet<int>? ItemIds { get; init; }
    public bool IsFamilyItem(int item) => ItemIds?.Contains(item) == true;

    internal static ImmutableDictionary<int, DungeonEpicDropBuffDefinition> Load(PvfArchive archive) => Load(VariousBuffCatalog.Load(archive));

    internal static ImmutableDictionary<int, DungeonEpicDropBuffDefinition> Load(VariousBuffCatalog catalog) => catalog.Items
        .Where(pair => pair.Value.Supported && !EpicDropBuffDefinition.IsLegacyView(pair.Value) &&
            !HellLegendEpicDropBuffDefinition.IsLegacyView(pair.Value)).ToImmutableDictionary(pair => pair.Key,
            pair => Convert(pair.Value) with { ItemIds = catalog.Families[pair.Value.SystemGroup] });

    internal static ImmutableDictionary<int, DungeonEpicDropBuffDefinition> ReadDefinitions(ImmutableArray<PvfTokenSection> rows,
        Func<int, string> resolve) => VariousBuffCatalog.ReadDefinitions(rows, resolve).Values.Where(buff => buff.Supported)
        .ToImmutableDictionary(buff => buff.BuffId, Convert);

    internal static DungeonEpicDropBuffDefinition Convert(VariousBuffDefinition buff)
    {
        var rates = ImmutableDictionary.CreateBuilder<DungeonEpicDropScope, int>();
        foreach (var effect in buff.Effects)
        {
            DungeonEpicDropScope? scope = effect.DropEffect switch
            {
                VariousBuffDropEffect.OrdinaryEpic => DungeonEpicDropScope.Ordinary,
                VariousBuffDropEffect.InfernoEpic => DungeonEpicDropScope.Inferno,
                VariousBuffDropEffect.ParticularEpic => DungeonEpicDropScope.Particular,
                VariousBuffDropEffect.DaybreakEpic => DungeonEpicDropScope.Daybreak,
                VariousBuffDropEffect.HellLegendEpic => DungeonEpicDropScope.HellLegend,
                _ => null
            };
            if (scope is { } mode) rates.Add(mode, effect.Parameters[0]);
        }
        return new(buff.BuffId, rates.ToImmutable(), buff.Percent(VariousBuffDropEffect.Boss))
            { SystemGroup = buff.SystemGroup, Resource = buff };
    }

    internal static ImmutableDictionary<int, DungeonEpicDropBuffDefinition> ReadMappings(ImmutableArray<PvfTokenSection> rows,
        IReadOnlyDictionary<int, DungeonEpicDropBuffDefinition> definitions) => rows
        .Where(row => row.Values is [{ Type: 0, Value: > 0 }]).GroupBy(row => row.Values[0].Value)
        .Where(group => group.Count() == 1 && rows.Count(row => row.Values.Any(value => value.Type == 0 && value.Value == group.Key)) == 1 &&
            group.Single() is { IsBlock: true, Children: [{ Name: "buff list", Children.Length: 0,
                Values: [{ Type: 0 } buff] }] } && definitions.ContainsKey(buff.Value))
        .ToImmutableDictionary(group => group.Key, group => definitions[group.Single().Children[0].Values[0].Value]);
}
