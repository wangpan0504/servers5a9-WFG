using System.Collections.Concurrent;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record ItemRecipeDefinition(int BlueprintItemId, bool CatalogDirect,
    ImmutableArray<ItemMaterialCost> Inputs, ImmutableArray<ItemMaterialCost> Outputs)
{
    public string? MaintainAbility { get; init; }
    public bool MaintainSocketStat { get; init; }
    public bool SetsDisjointInstanceType { get; init; }
    public int SuccessRate { get; init; }
    public ImmutableArray<ItemMaterialCost> FailureOutputs { get; init; } = [];
    public byte? MakingType { get; init; }
    public byte MakingLevel { get; init; }
    public ImmutableArray<ItemMaterialCost> Skills { get; init; } = [];
    public ImmutableArray<ImmutableArray<int>> UpgradeTransitions { get; init; } = [];
}

internal sealed class ItemRecipeCatalog
{
    readonly PvfArchive? archive;
    readonly IReadOnlyDictionary<int, string>? stackable;
    readonly ConcurrentDictionary<int, Lazy<ItemRecipeDefinition?>> definitions = new();

    public ItemRecipeCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> stackable)
    { this.archive = archive; this.stackable = stackable; }

    internal ItemRecipeCatalog(IEnumerable<ItemRecipeDefinition> source)
    {
        foreach (var definition in source) definitions.TryAdd(definition.BlueprintItemId, new(() => definition));
    }

    public ItemRecipeDefinition? Get(int id)
    {
        if (id <= 0) return null;
        if (definitions.TryGetValue(id, out var known)) return known.Value;
        if (stackable?.ContainsKey(id) != true) return null;
        return definitions.GetOrAdd(id, key => new(() => Read(key))).Value;
    }

    ItemRecipeDefinition? Read(int id)
    {
        if (archive is null || stackable is null || !stackable.TryGetValue(id, out var path)) return null;
        string resource = "stackable/" + path;
        var file = ItemRecipeFile.Parse(id, resource, archive.ReadTokens(resource), archive.ResolveString);
        var definition = Parse(file);
        if (definition is null || file.CustomUpgradeType is null) return definition;
        // 四件旧活动图必须配套隔离资源补丁，转换为有明确输入/产物的标准制作图。
        // 本服选择用矩阵的行/列表示强化等级，不把尚未确认的原生action98当成已还原。
        if (file.CustomUpgradeType is not ("infinite_upgrade_event_20_level" or "infinite_upgrade_event_30_level" or
            "infinite_upgrade_event_40_level" or "infinite_upgrade_event_50_level") ||
            definition.MaintainAbility != "normal" || !definition.MaintainSocketStat ||
            definition.Inputs is not [{ ItemId: 102030279, Count: 1 }] ||
            definition.Outputs is not [{ ItemId: 102030279, Count: 1 }] || definition.SuccessRate != 0) return null;
        var document = new PvfTokenDocument(archive.ReadTokens("etc/(r)serverparameter.etc"), archive.ResolveString);
        var values = document.Sections.Where(section => section.Name == "recipe custom upgrade table").SelectMany(section => section.Children)
            .Single(section => section.Name == "table type" && section.Values.Length > 0 &&
            section.Values[0].Type == 6 && archive.ResolveString(section.Values[0].Value) == file.CustomUpgradeType).Values.Skip(1).ToArray();
        if (values.Length != 32 * 33 || values.Any(value => value.Type != 0 || value.Value < 0))
            throw new InvalidDataException("活动重铸矩阵无效");
        var rows = ImmutableArray.CreateBuilder<ImmutableArray<int>>();
        for (int row = 0; row < 32; row++)
        {
            var weights = values.Skip(row * 33 + 1).Take(32).Select(value => value.Value).ToImmutableArray();
            if (values[row * 33].Value != row || weights.Sum(value => (long)value) != 1000000)
                throw new InvalidDataException("活动重铸权重必须完整且合计百万");
            rows.Add(weights);
        }
        return definition with { UpgradeTransitions = rows.ToImmutable() };
    }

    internal static ItemRecipeDefinition? Parse(ItemRecipeFile file)
    {
        if (!file.Diagnostics.IsEmpty || file.SourceType is not (0 or 1 or 2 or 5) || file.Inputs.IsEmpty || file.Outputs.IsEmpty ||
            file.Outputs.Length > byte.MaxValue || file.FailureOutputs.Length > byte.MaxValue || file.SuccessRate is < 0 or > 10000 ||
            file.MaintainAbility is not (null or "normal") || file.Skills.Any(row => row.ItemId is < 0 or > ushort.MaxValue || row.Count > byte.MaxValue))
            return null;
        byte? type = null;
        if (file.MakingType is { } making)
        {
            if (!ItemMakingSkillCatalog.TryType(making, out byte actual) || file.MakingLevel is < 1 or > byte.MaxValue) return null;
            type = actual;
        }
        ImmutableArray<ItemMaterialCost> Merge(ImmutableArray<ItemRecipeEntry> entries)
        {
            var totals = new Dictionary<int, long>();
            foreach (var entry in entries)
            {
                long count = totals.GetValueOrDefault(entry.ItemId) + entry.Count;
                if (entry.ItemId < 0 || entry.Count <= 0 || count > int.MaxValue) throw new InvalidDataException("设计图数量溢出");
                totals[entry.ItemId] = count;
            }
            return totals.Select(row => new ItemMaterialCost(row.Key, (int)row.Value)).ToImmutableArray();
        }
        // 旧chemistry/craftmanship目录也使用ID源制作请求；实体图仍可走槽位源。
        return new(file.TemplateId, file.SourceType is 0 or 2 or 5, Merge(file.Inputs), Merge(file.Outputs))
        { MaintainAbility = file.MaintainAbility, MaintainSocketStat = file.MaintainSocketStat,
            SetsDisjointInstanceType = file.SetsDisjointInstanceType,
            SuccessRate = file.SuccessRate, FailureOutputs = Merge(file.FailureOutputs), MakingType = type, MakingLevel = checked((byte)file.MakingLevel),
            Skills = file.Skills.Select(row => new ItemMaterialCost(row.ItemId, row.Count)).ToImmutableArray() };
    }
}
