using System.Collections.Frozen;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal readonly record struct EquipmentRandomOptionValueRatio(float Lower, float Upper, int Weight);
internal sealed record EquipmentRandomOptionChangeGroup(long Weight, ImmutableArray<(byte Type, long Weight)> Options);
internal sealed record EquipmentRandomOptionChangeType(long Weight, ImmutableArray<EquipmentRandomOptionChangeGroup> Groups,
    int MinimumValue, int MaximumValue);
internal sealed record EquipmentRandomOptionChangeDefinition(uint GoldCost, ImmutableArray<EquipmentRandomOptionChangeType> Types,
    ImmutableArray<EquipmentRandomOptionValueRatio> ValueRatios);

internal sealed partial class RandomOptionCatalog
{
    sealed record ChangeRules(ImmutableArray<RandomOptionTypeRow> Types, ImmutableArray<RandomOptionGroupRow> Selections,
        ImmutableArray<RandomOptionModificationCostRow> Costs, FrozenDictionary<(int Rarity, int Type), RandomOptionRangeRow> Ranges,
        ImmutableArray<EquipmentRandomOptionValueRatio> Ratios);

    readonly ChangeRules? changeRules;

    static ChangeRules? ReadChangeRules(Func<string, PvfToken[]> read, Func<int, string> resolve)
    {
        ImmutableArray<PvfTokenSection> Sections(string path) => new PvfTokenDocument(read(Root + path), resolve).Sections;
        ImmutableArray<PvfTokenSection> overall1;
        try { overall1 = Sections("randomizedoptionoverall1.etc"); }
        catch (FileNotFoundException) { return null; }
        if (overall1.SingleOrDefault(row => row.Name == "modified option type") is not { } typeSection)
            return null;
        var selection = Sections("optiongroupselection.etc").SingleOrDefault(row => row.Name == "modified option selection");
        var overall2 = Sections("randomizedoptionoverall2.etc");
        var costSection = overall2.SingleOrDefault(row => row.Name == "option modification");
        if (selection is null || costSection is null) return null;
        var types = RandomOptionFiles.Types(typeSection);
        var costs = RandomOptionFiles.ModificationCosts(costSection);
        var ranges = RandomOptionFiles.Ranges(overall2.Single(row => row.Name == "different weight"));
        var ratios = RandomOptionFiles.ValueRatios(Sections("optionnumbering.etc").Single(row => row.Name == "option value ratio"));
        if (types.Any(row => row.Level < 0 || row.Weights.Any(value => value < 0) || row.Weights.Sum(value => (long)value) <= 0) ||
            types.GroupBy(row => (row.Rarity, row.ItemType, row.Level)).Any(rows => rows.Count() != 1) ||
            costs.Any(row => row.Level < 0 || row.CommonGold < 0 || row.UniqueGold < 0) ||
            costs.Zip(costs.Skip(1)).Any(pair => pair.First.Level >= pair.Second.Level) ||
            ranges.GroupBy(row => (row.Rarity, row.OptionType)).Any(rows => rows.Count() != 1) ||
            ratios.Any(row => !float.IsFinite(row.Lower) || !float.IsFinite(row.Upper) || row.Lower < 0 || row.Upper < row.Lower ||
                row.Upper > 1 || row.Weight < 0) || ratios.Sum(row => (long)row.Weight) <= 0)
            throw new InvalidDataException("装备随机属性变换配置无效");
        return new(types, RandomOptionFiles.Groups(selection, resolve), costs,
            ranges.ToFrozenDictionary(row => (row.Rarity, row.OptionType)),
            ratios.Select(row => new EquipmentRandomOptionValueRatio(row.Lower, row.Upper, row.Weight)).ToImmutableArray());
    }

    public EquipmentRandomOptionChangeDefinition? ResolveChange(ItemTemplate template)
    {
        if (changeRules is not { } rules || template.Kind != ItemKind.Equipment || template.Rarity is not (2 or 3) ||
            !equipment.TryGetValue(template.Id, out var path)) return null;
        var document = EquipmentFile.ReadScript(read, "equipment/" + path);
        if (!document.TryGetInt32("random option", out int enabled) || enabled <= 0 ||
            !document.TryGetString("item group name", out string part) || string.IsNullOrWhiteSpace(part)) return null;
        const int itemType = 1;
        var type = rules.Types.Where(row => row.Rarity == template.Rarity && row.Level == template.MinimumLevel &&
            row.ItemType is -1 or itemType).OrderByDescending(row => row.ItemType == -1).FirstOrDefault();
        // 本版按原序取第一个不低于装备需求等级的费用档位，不能向下套用解封费用。
        var price = rules.Costs.Where(row => row.Level >= template.MinimumLevel)
            .Select(row => (RandomOptionModificationCostRow?)row).FirstOrDefault();
        if (type.Weights.IsDefault || price is null) return null;
        var resolved = ImmutableArray.CreateBuilder<EquipmentRandomOptionChangeType>();
        for (int i = 0; i < type.Weights.Length; i++)
        {
            if (type.Weights[i] == 0) { resolved.Add(new(0, [], 0, 0)); continue; }
            var selection = rules.Selections.Where(row => row.Rarity == template.Rarity && row.OptionType == i + 1 &&
                row.Part.Equals(part, StringComparison.OrdinalIgnoreCase) && row.ItemType is -1 or itemType)
                .OrderByDescending(row => row.ItemType == -1).FirstOrDefault();
            if (selection.Groups.IsDefault || !rules.Ranges.TryGetValue((template.Rarity, i + 1), out var span) ||
                span.Minimum < 0 || span.Maximum < span.Minimum || span.Maximum > 255) return null;
            var pools = ImmutableArray.CreateBuilder<EquipmentRandomOptionChangeGroup>();
            for (int j = 0; j < selection.Groups.Length; j++)
            {
                if (selection.Weights[j] == 0) continue;
                if (selection.Weights[j] < 0 || !groups.TryGetValue(selection.Groups[j], out var options) ||
                    options.Any(row => row.Id is <= 0 or > 255 || row.Weight < 0) || options.Sum(row => row.Weight) <= 0) return null;
                pools.Add(new(selection.Weights[j], options.Select(row => ((byte)row.Id, row.Weight)).ToImmutableArray()));
            }
            if (pools.Count == 0) return null;
            resolved.Add(new(type.Weights[i], pools.ToImmutable(), span.Minimum, span.Maximum));
        }
        return new((uint)(template.Rarity == 2 ? price.Value.CommonGold : price.Value.UniqueGold), resolved.ToImmutable(), rules.Ratios);
    }
}
