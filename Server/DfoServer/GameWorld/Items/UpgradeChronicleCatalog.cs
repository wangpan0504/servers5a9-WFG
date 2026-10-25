using System.Collections.Concurrent;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

// 单组升级配方：Accepts 是 0 号槽允许放入的装备，Materials 是其余槽位的固定材料，
// 产物固定取 [output item] 的第一条（本版 PVF 每组只有一条）。
// 设计图自身的 [need material] 是 NPC 商店卖价（异形结晶），不是使用消耗，这里不读取。
internal sealed record UpgradeChronicleRecipe(int Group, ImmutableArray<int> Accepts,
    ImmutableArray<ItemMaterialCost> Materials, int OutputItemId, int OutputCount);

internal sealed record UpgradeChronicleDefinition(int BlueprintItemId, ImmutableArray<UpgradeChronicleRecipe> Recipes, int TicketType = 21)
{
    public UpgradeChronicleRecipe? Find(int targetItemId)
    {
        foreach (var recipe in Recipes)
            if (recipe.Accepts.Contains(targetItemId))
                return recipe;
        return null;
    }
}

// 设计图按需解析：只有真正被请求的设计图才读 PVF，不扫描整份 stackable.lst。
internal sealed class UpgradeChronicleCatalog
{
    readonly PvfArchive? archive;
    readonly IReadOnlyDictionary<int, string>? stackable;
    readonly IReadOnlyDictionary<int, string>? equipment;
    readonly ConcurrentDictionary<int, Lazy<EquipmentEmancipationFile?>> conversions = new();
    readonly ConcurrentDictionary<int, Lazy<UpgradeChronicleDefinition?>> definitions = new();

    public UpgradeChronicleCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> stackable,
        IReadOnlyDictionary<int, string>? equipment = null)
    {
        this.archive = archive;
        this.stackable = stackable;
        this.equipment = equipment;
    }

    internal UpgradeChronicleCatalog(IEnumerable<UpgradeChronicleDefinition> source)
    {
        foreach (var definition in source)
            definitions.TryAdd(definition.BlueprintItemId, new(() => definition));
    }

    public UpgradeChronicleDefinition? Get(int blueprintItemId) =>
        blueprintItemId <= 0 ? null :
        definitions.GetOrAdd(blueprintItemId, id => new(() => Read(id))).Value;

    public UpgradeChronicleRecipe? Find(UpgradeChronicleDefinition definition, int targetItemId)
    {
        if (definition.TicketType == 21)
            return definition.Find(targetItemId);
        // 其余 ticket 包含成长、补充能量等不同规则，不按普通转换书擅自放行。
        if (definition.TicketType != 23 || archive is null || equipment is null)
            return null;
        var conversion = conversions.GetOrAdd(targetItemId, id => new(() =>
        {
            if (!equipment.TryGetValue(id, out var path)) return null;
            return EquipmentEmancipationFile.Parse(archive.ReadTokens("equipment/" + path), archive.ResolveString);
        })).Value;
        if (conversion is not { Type: 23, Outputs.Length: 1 } || conversion.Outputs[0].Count != 1)
            return null;
        return new(0, [targetItemId], conversion.Inputs.Select(input => new ItemMaterialCost(input.ItemId, input.Count)).ToImmutableArray(),
            conversion.Outputs[0].ItemId, 1);
    }

    UpgradeChronicleDefinition? Read(int blueprintItemId)
    {
        if (archive is null || stackable is null || !stackable.TryGetValue(blueprintItemId, out var path))
            return null;
        string resource = "stackable/" + path;
        var tokens = archive.ReadTokens(resource);
        var document = new PvfTokenDocument(tokens, archive.ResolveString);
        var tickets = document.Sections.Where(section => section.Name == "emancipate ticket").ToArray();
        if (!document.Diagnostics.IsDefaultOrEmpty || tickets.Length != 1 || tickets[0].Values.Length != 1 ||
            tickets[0].Values[0].Type != 0)
            return null;
        int ticket = tickets[0].Values[0].Value;
        if (ticket == 23) return new(blueprintItemId, [], ticket);
        if (ticket != 21) return null;
        var file = UpgradeChronicleFile.Parse(blueprintItemId, resource, tokens, archive.ResolveString);
        if (!file.Diagnostics.IsDefaultOrEmpty || file.Groups.IsDefaultOrEmpty)
            return null;
        var recipes = ImmutableArray.CreateBuilder<UpgradeChronicleRecipe>();
        foreach (var group in file.Groups)
        {
            var accepts = ImmutableArray.CreateBuilder<int>();
            var materials = ImmutableArray.CreateBuilder<ItemMaterialCost>();
            foreach (var entry in group.Inputs)
            {
                if (entry.ItemId <= 0 || entry.Count <= 0)
                    return null;
                if (entry.Slot == 0)
                    accepts.Add(entry.ItemId);
                else
                    materials.Add(new(entry.ItemId, entry.Count));
            }
            // 每组必须能选出一件装备、且有唯一产物，否则这一组不可用。
            if (accepts.Count == 0 || group.Outputs.Length != 1 || group.Outputs[0].ItemId <= 0 || group.Outputs[0].Count != 1)
                return null;
            recipes.Add(new(group.Index, accepts.ToImmutable(), materials.ToImmutable(), group.Outputs[0].ItemId, group.Outputs[0].Count));
        }
        return recipes.Count == 0 ? null : new(blueprintItemId, recipes.ToImmutable());
    }
}
