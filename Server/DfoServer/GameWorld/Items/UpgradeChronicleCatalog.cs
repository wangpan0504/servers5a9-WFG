using System.Collections.Concurrent;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

// 单组升级配方：Accepts 是 0 号槽允许放入的装备，Materials 是其余槽位的固定材料，
// 产物固定取 [output item] 的第一条（本版 PVF 每组只有一条）。
// 设计图自身的 [need material] 是 NPC 商店卖价（异形结晶），不是使用消耗，这里不读取。
internal sealed record UpgradeChronicleRecipe(int Group, ImmutableArray<int> Accepts,
    ImmutableArray<ItemMaterialCost> Materials, int OutputItemId, int OutputCount)
{
    public int RequiredGrowthExperience { get; init; }
}

internal sealed record UpgradeChronicleDefinition(int BlueprintItemId, ImmutableArray<UpgradeChronicleRecipe> Recipes, int TicketType = 21)
{
    public int MaximumUpgrade { get; init; } = -1;
    public int MaximumAmplify { get; init; } = -1;
    public int MaximumRefinement { get; init; } = -1;
    public EquipmentLevelTicket? LevelTicket { get; init; }
    public ImmutableDictionary<int, ImmutableHashSet<int>> GrowthStages { get; init; } = ImmutableDictionary<int, ImmutableHashSet<int>>.Empty;
    public int MaterialRevision { get; init; }
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
    readonly Lazy<EquipmentLevelUpgradeRules?> levelRules;
    readonly Lazy<Dictionary<(int Group, int Stage), int>> growthLimits;

    public UpgradeChronicleCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> stackable,
        IReadOnlyDictionary<int, string>? equipment = null)
    {
        this.archive = archive;
        this.stackable = stackable;
        this.equipment = equipment;
        levelRules = new(() => archive.Contains("etc/equipmentlevelemancipate.etc")
            ? new(archive.ReadText("etc/equipmentlevelemancipate.etc")) : null);
        growthLimits = new(() => ReadGrowthLimits(archive));
    }

    internal UpgradeChronicleCatalog(IEnumerable<UpgradeChronicleDefinition> source)
    {
        levelRules = new(() => null);
        growthLimits = new(() => []);
        foreach (var definition in source)
            definitions.TryAdd(definition.BlueprintItemId, new(() => definition));
    }

    public ItemMaterialCost? LevelCost(int rarity, int slot, int level, int upgrade, int amplify, int refinement) =>
        levelRules.Value?.Cost(rarity, slot, level, upgrade, amplify, refinement);

    public UpgradeChronicleDefinition? Get(int blueprintItemId) =>
        blueprintItemId <= 0 ? null :
        definitions.GetOrAdd(blueprintItemId, id => new(() => Read(id))).Value;

    public UpgradeChronicleRecipe? Find(UpgradeChronicleDefinition definition, int targetItemId)
    {
        if (!definition.Recipes.IsEmpty)
            return definition.Find(targetItemId);
        if (archive is null || equipment is null)
            return null;
        var conversion = conversions.GetOrAdd(targetItemId, id => new(() =>
        {
            if (!equipment.TryGetValue(id, out var path)) return null;
            return EquipmentEmancipationFile.Parse(archive.ReadTokens("equipment/" + path), archive.ResolveString);
        })).Value;
        int expected = definition.TicketType == 1 ? 0 : definition.TicketType;
        if (conversion is not { Outputs.Length: 1 } || conversion.Outputs[0].Count != 1)
            return null;
        int required = 0;
        if (definition.TicketType is 2 or 3)
        {
            if (!definition.GrowthStages.TryGetValue(conversion.GrowGroup, out var stages) || !stages.Contains(conversion.GrowStage) ||
                !growthLimits.Value.TryGetValue((conversion.GrowGroup, conversion.GrowStage), out required) || required <= 0) return null;
        }
        else if (conversion.GrowGroup != 0 || conversion.Type != expected) return null;
        var materials = conversion.Inputs.Select(input => new ItemMaterialCost(input.ItemId,
            checked((int)((long)input.Count * (100 + definition.MaterialRevision) / 100)))).ToImmutableArray();
        return new(0, [targetItemId], materials, conversion.Outputs[0].ItemId, 1) { RequiredGrowthExperience = required };
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
        int? Maximum(string tag)
        {
            var fields = document.Sections.Where(section => section.Name == tag).ToArray();
            return fields.Length == 0 ? -1 : fields is [{ Values: [{ Type: 0, Value: >= -1 and <= 127 } value] }]
                ? value.Value : null;
        }
        int? upgrade = Maximum("emancipate grade max"), amplify = Maximum("emancipate amplify max"), refinement = Maximum("emancipate genuineGrade max");
        if (upgrade is null || amplify is null || refinement is null) return null;
        if (ticket is 2 or 3)
        {
            var growth = document.Sections.Where(section => section.Name == "emancipate grow apply").ToArray();
            if (growth is not [{ IsBlock: true } block]) return null;
            var stages = ImmutableDictionary.CreateBuilder<int, ImmutableHashSet<int>>();
            foreach (var group in block.Children)
            {
                var values = group.Values;
                if (group.Name != "group" || values.Any(token => token.Type != 0 || token.Value <= 0) || values.Length < 3 ||
                    values[1].Value != values.Length - 2 || !stages.TryAdd(values[0].Value, values.Skip(2).Select(token => token.Value).ToImmutableHashSet())) return null;
            }
            var revisions = document.Sections.Where(section => section.Name == "emancipate material revision").ToArray();
            if (revisions.Length > 1 || revisions.Length == 1 && revisions[0].Values is not [{ Type: 0, Value: -1 }, { Type: 0, Value: >= 0 and <= 10000 }]) return null;
            return new(blueprintItemId, [], ticket) { GrowthStages = stages.ToImmutable(),
                MaterialRevision = revisions.Length == 0 ? 0 : revisions[0].Values[1].Value };
        }
        if (ticket == 5)
            return EquipmentLevelUpgradeRules.Parse(document) is { } level ? new(blueprintItemId, [], ticket) { LevelTicket = level } : null;
        if (ticket is 0 or 1 or 4 or >= 6 and <= 20 or 22 or 23)
            return new(blueprintItemId, [], ticket) { MaximumUpgrade = upgrade.Value, MaximumAmplify = amplify.Value,
                MaximumRefinement = refinement.Value };
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

    static Dictionary<(int Group, int Stage), int> ReadGrowthLimits(PvfArchive archive)
    {
        var result = new Dictionary<(int, int), int>();
        if (!archive.Contains("etc/equipment_grow.etc")) return result;
        var document = new PvfTokenDocument(archive.ReadTokens("etc/equipment_grow.etc"), archive.ResolveString);
        foreach (var group in document.Sections.Where(section => section.Name == "group"))
        {
            if (group.Values is not [{ Type: 0, Value: > 0 } id]) throw new InvalidDataException("装备成长组无效");
            foreach (var stage in group.Children.Where(section => section.Name == "exp base"))
            {
                if (stage.Values is not [{ Type: 0, Value: > 0 } number, { Type: 0, Value: >= 0 and <= short.MaxValue } limit, { Type: 0 }] ||
                    !result.TryAdd((id.Value, number.Value), limit.Value)) throw new InvalidDataException("装备成长经验表无效");
            }
        }
        return result;
    }
}
