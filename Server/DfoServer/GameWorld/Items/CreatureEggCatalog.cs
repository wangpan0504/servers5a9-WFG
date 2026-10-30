using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record CreatureEggDefinition(int EggItemId, int OutputItemId, int Species)
{
    public byte OutputMode { get; init; }
    public string DefaultName { get; init; } = "";
}

internal sealed class CreatureEggCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> equipment, ItemCatalog items)
{
    readonly ConcurrentDictionary<int, CreatureEggDefinition> definitions = new();

    public CreatureEggDefinition? Get(int itemId)
    {
        if (definitions.TryGetValue(itemId, out var cached)) return cached;
        if (!equipment.TryGetValue(itemId, out string? sourcePath)) return null;
        var source = EquipmentFile.ReadScript(archive.ReadText, "equipment/" + sourcePath);
        if (!source.TryGetInt32("sub type", out int subtype) || subtype != 1 ||
            !source.TryGetInt32("output index", out int output) || output <= 0 || output == itemId ||
            !equipment.TryGetValue(output, out string? targetPath)) return null;
        var target = EquipmentFile.ReadScript(archive.ReadText, "equipment/" + targetPath);
        var definition = Read(source, target, items.Get(itemId), items.Get(output));
        return definition is null ? null : definitions.GetOrAdd(itemId, definition);
    }

    internal static CreatureEggDefinition? Read(PvfDocument source, PvfDocument target, ItemTemplate egg, ItemTemplate output)
    {
        if (egg.Kind != ItemKind.Creature || output.Kind != ItemKind.Creature || egg.EquipmentSlot != 26 || output.EquipmentSlot != 26 ||
            !CreatureType(source) || !CreatureType(target) ||
            egg.Id == output.Id || !source.TryGetInt32("sub type", out int subtype) || subtype != 1 ||
            !source.TryGetInt32("output index", out int outputId) || outputId != output.Id ||
            !source.TryGetInt32("creature species", out int species) || species < 0 ||
            !target.TryGetInt32("sub type", out int targetSubtype) || targetSubtype != 0 ||
            !target.TryGetInt32("creature species", out int targetSpecies) || targetSpecies != species || output.CreatureInitialMode > 1)
            return null;
        return new(egg.Id, output.Id, species) { OutputMode = output.CreatureInitialMode,
            DefaultName = output.CreatureName ?? output.Name };
    }

    static bool CreatureType(PvfDocument document) => Regex.IsMatch(document.Section("equipment type"),
        @"^\s*`\[creature\]`\s+0\s*$", RegexOptions.CultureInvariant);
}
