using System.Collections.Concurrent;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record EquipmentGrowthStage(int Group, int Stage, int Limit, bool Automatic,
    int Output, int Point, IReadOnlyDictionary<int, int> KindRates, int[] DifficultyRates);

// 装备自身的成长声明按需读取，不扫描装备全集；终阶没有下一阶段，不能继续积累。
internal sealed class EquipmentGrowthCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> equipment)
{
    readonly ConcurrentDictionary<int, Lazy<EquipmentGrowthStage?>> stages = new();
    readonly Lazy<PvfTokenDocument?> rules = new(() => archive.Contains("etc/equipment_grow.etc")
        ? new(archive.ReadTokens("etc/equipment_grow.etc"), archive.ResolveString) : null);

    public EquipmentGrowthStage? Get(int id) => stages.GetOrAdd(id, key => new(() => Read(key))).Value;

    EquipmentGrowthStage? Read(int id)
    {
        if (!equipment.TryGetValue(id, out var path)) return null;
        var conversion = EquipmentEmancipationFile.Parse(archive.ReadTokens("equipment/" + path), archive.ResolveString);
        if (conversion is not { GrowGroup: > 0, GrowStage: > 0, Outputs: [{ Count: 1 } output] }) return null;
        var group = rules.Value?.Sections.SingleOrDefault(section => section.Name == "group" &&
            section.Values is [{ Type: 0 } key] && key.Value == conversion.GrowGroup);
        if (group is null) return null;
        var stage = group.Children.SingleOrDefault(section => section.Name == "exp base" &&
            section.Values.Length == 3 && section.Values[0].Value == conversion.GrowStage);
        if (stage?.Values is not [{ Type: 0 }, { Type: 0, Value: > 0 and <= short.MaxValue } limit,
            { Type: 0, Value: 0 or 1 } automatic]) return null;
        var acquire = group.Children.Single(section => section.Name == "exp acquire");
        var point = acquire.Children.Single(section => section.Name == "point").Values;
        var kind = acquire.Children.Single(section => section.Name == "kind").Values;
        var difficulty = acquire.Children.Single(section => section.Name == "difficulty correction").Values;
        if (point is not [{ Type: 0, Value: > 0 and <= 100 } value] || kind.Length < 3 ||
            kind.Any(token => token.Type != 0 || token.Value < 0) || kind[0].Value * 2 + 1 != kind.Length ||
            difficulty.IsEmpty || difficulty.Any(token => token.Type != 0 || token.Value < 0))
            throw new InvalidDataException("装备成长经验规则无效");
        var rates = new Dictionary<int, int>();
        for (int i = 1; i < kind.Length; i += 2) rates.Add(kind[i].Value, kind[i + 1].Value);
        return new(conversion.GrowGroup, conversion.GrowStage, limit.Value, automatic.Value == 1,
            output.ItemId, value.Value, rates, difficulty.Select(token => token.Value).ToArray());
    }
}
