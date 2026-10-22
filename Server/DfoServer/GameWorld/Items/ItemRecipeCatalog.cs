using System.Collections.Concurrent;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

// 普通[recipe]设计图：第一对输入是被改造的装备，其余是固定材料，产物唯一。
internal sealed record ItemRecipeDefinition(int BlueprintItemId, int TargetItemId,
    ImmutableArray<ItemMaterialCost> Materials, int OutputItemId, int OutputCount);

// 按需解析：只有真正被请求的设计图才读 PVF，不扫描整份 stackable.lst。
internal sealed class ItemRecipeCatalog
{
    readonly PvfArchive? archive;
    readonly IReadOnlyDictionary<int, string>? stackable;
    readonly ConcurrentDictionary<int, Lazy<ItemRecipeDefinition?>> definitions = new();

    public ItemRecipeCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> stackable)
    {
        this.archive = archive;
        this.stackable = stackable;
    }

    internal ItemRecipeCatalog(IEnumerable<ItemRecipeDefinition> source)
    {
        foreach (var definition in source)
            definitions.TryAdd(definition.BlueprintItemId, new(() => definition));
    }

    public ItemRecipeDefinition? Get(int blueprintItemId) =>
        blueprintItemId <= 0 ? null :
        definitions.GetOrAdd(blueprintItemId, id => new(() => Read(id))).Value;

    ItemRecipeDefinition? Read(int blueprintItemId)
    {
        if (archive is null || stackable is null || !stackable.TryGetValue(blueprintItemId, out var path))
            return null;
        string resource = "stackable/" + path;
        var file = ItemRecipeFile.Parse(blueprintItemId, resource, archive.ReadText(resource));
        if (!file.Diagnostics.IsDefaultOrEmpty || file.Inputs.IsDefaultOrEmpty || file.Output is not { } output ||
            file.Inputs[0].Count != 1 || output.Count != 1 || output.ItemId <= 0)
            return null;
        var materials = ImmutableArray.CreateBuilder<ItemMaterialCost>();
        // 其它材料同名要合并，避免同编号多行被扣两次最小值而不是合计。
        var totals = new Dictionary<int, long>();
        foreach (var entry in file.Inputs.Skip(1))
        {
            if (entry.ItemId <= 0 || entry.Count <= 0)
                return null;
            long sum = totals.GetValueOrDefault(entry.ItemId) + entry.Count;
            if (sum > int.MaxValue)
                return null;
            totals[entry.ItemId] = sum;
        }
        foreach (var (itemId, count) in totals)
            materials.Add(new(itemId, (int)count));
        return new(blueprintItemId, file.Inputs[0].ItemId, materials.ToImmutable(), output.ItemId, output.Count);
    }
}
