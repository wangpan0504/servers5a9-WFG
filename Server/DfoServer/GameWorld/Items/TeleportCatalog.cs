using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record TeleportPotionDefinition(int MinimumLevel, int CoolTime, ImmutableHashSet<int> DungeonMaps);
internal sealed record SoloTeleportDefinition(int CoolSeconds);

// 与物品目录共享LST及归档；只解析本次实际请求涉及的脚本。
internal sealed class TeleportCatalog(Func<string, string> read, IReadOnlyDictionary<int, string> equipment,
    IReadOnlyDictionary<int, string> stackable)
{
    readonly ConcurrentDictionary<int, Lazy<TeleportPotionDefinition?>> potions = new();
    readonly ConcurrentDictionary<int, Lazy<SoloTeleportDefinition?>> auras = new();

    public TeleportPotionDefinition? Potion(int id) => !stackable.ContainsKey(id) ? null : potions.GetOrAdd(id, key => new(() =>
        ReadPotion(new PvfDocument(read("stackable/" + stackable[key]))))).Value;

    public SoloTeleportDefinition? Aura(int id) => !equipment.ContainsKey(id) ? null : auras.GetOrAdd(id, key => new(() =>
        ReadAura(EquipmentFile.ReadScript(read, "equipment/" + equipment[key])))).Value;

    internal static TeleportPotionDefinition? ReadPotion(PvfDocument document)
    {
        if (document.Section("stackable type").Trim() != "`[teleport potion]` 0" ||
            document.Has("expiration date") || document.Has("npc teleport potion"))
            return null;
        int[] minimum = PvfScript.Integers(document.Section("minimum level")), cool = PvfScript.Integers(document.Section("cool time"));
        int[] maps = PvfScript.Integers(document.Section("int data"));
        if (minimum is not [>= 1 and <= 95] || cool is not [> 0] || maps.Length == 0 || maps.Any(id => id <= 0))
            return null;
        return new(minimum[0], cool[0], maps.ToImmutableHashSet());
    }

    internal static SoloTeleportDefinition? ReadAura(PvfDocument document)
    {
        if (document.Section("equipment type").Trim() != "`[aurora avatar]` 0") return null;
        var match = Regex.Match(document.Section("aura ability"), @"^\s*`\[solo teleport\]`\s+(\d+)\s*$");
        return match.Success && int.TryParse(match.Groups[1].Value, out int seconds) && seconds is > 0 and <= 86400
            ? new(seconds) : null;
    }
}
