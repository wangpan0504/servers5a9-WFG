using System.Collections.Concurrent;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record TeleportPotionDefinition(int MinimumLevel, int CoolTime, ImmutableHashSet<int> DungeonMaps);
internal enum AuraAbilityKind { None, PartyTeleport, SoloTeleport, UpgradeSoloTeleport }
internal sealed record AuraAbilityDefinition(AuraAbilityKind Kind, int CoolSeconds)
{
    public bool Personal => Kind is AuraAbilityKind.SoloTeleport or AuraAbilityKind.UpgradeSoloTeleport;
}

// 与物品目录共享LST及归档；只解析本次实际请求涉及的脚本。
internal sealed class TeleportCatalog(Func<string, string> read, IReadOnlyDictionary<int, string> equipment,
    IReadOnlyDictionary<int, string> stackable)
{
    readonly ConcurrentDictionary<int, Lazy<TeleportPotionDefinition?>> potions = new();
    readonly ConcurrentDictionary<int, Lazy<AuraAbilityDefinition?>> auras = new();

    public TeleportPotionDefinition? Potion(int id) => !stackable.ContainsKey(id) ? null : potions.GetOrAdd(id, key => new(() =>
        ReadPotion(new PvfDocument(read("stackable/" + stackable[key]))))).Value;

    public AuraAbilityDefinition? Aura(int id) => !equipment.ContainsKey(id) ? null : auras.GetOrAdd(id, key => new(() =>
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

    internal static AuraAbilityDefinition? ReadAura(PvfDocument document)
    {
        if (document.Section("equipment type").Trim() != "`[aurora avatar]` 0") return null;
        var declaration = EquipmentAuraAbility.Read(document);
        if (declaration is null) return null;
        AuraAbilityKind? kind = declaration.Label switch
        {
            "[none]" => AuraAbilityKind.None,
            "[party teleport]" => AuraAbilityKind.PartyTeleport,
            "[solo teleport]" => AuraAbilityKind.SoloTeleport,
            "[upgrade solo teleport]" => AuraAbilityKind.UpgradeSoloTeleport,
            _ => null
        };
        if (kind is null) return null;
        if (kind == AuraAbilityKind.None)
            return declaration.Parameter is null ? new(kind.Value, 0) : null;
        // ARAD计时器将秒数乘1000后存入i32；0秒为有效的无冷却声明。
        return declaration.Parameter is >= 0 and <= int.MaxValue / 1000 ? new(kind.Value, declaration.Parameter.Value) : null;
    }
}
