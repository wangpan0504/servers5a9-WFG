using System.Collections.Concurrent;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record ItemRecipeDefinition(int BlueprintItemId, bool CatalogDirect,
    ImmutableArray<ItemMaterialCost> Inputs, ImmutableArray<ItemMaterialCost> Outputs)
{
    public string? MaintainAbility { get; init; }
    public bool MaintainSocketStat { get; init; }
    public int SuccessPercent { get; init; }
    public byte? MakingType { get; init; }
    public byte MakingLevel { get; init; }
    public ImmutableArray<ItemMaterialCost> Skills { get; init; } = [];
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
        return Parse(ItemRecipeFile.Parse(id, resource, archive.ReadTokens(resource), archive.ResolveString));
    }

    internal static ItemRecipeDefinition? Parse(ItemRecipeFile file)
    {
        if (!file.Diagnostics.IsEmpty || file.SourceType is not (0 or 1) || file.Inputs.IsEmpty || file.Outputs.IsEmpty ||
            file.Outputs.Length > byte.MaxValue || file.SuccessPercent is < 0 or > 100 ||
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
        return new(file.TemplateId, file.SourceType == 0, Merge(file.Inputs), Merge(file.Outputs))
        { MaintainAbility = file.MaintainAbility, MaintainSocketStat = file.MaintainSocketStat,
            SuccessPercent = file.SuccessPercent, MakingType = type, MakingLevel = checked((byte)file.MakingLevel),
            Skills = file.Skills.Select(row => new ItemMaterialCost(row.ItemId, row.Count)).ToImmutableArray() };
    }
}
