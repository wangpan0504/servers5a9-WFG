using System.Collections.Concurrent;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record EquipmentEffectDefinition(ItemTemplate Item, ushort Effect)
{
    internal static EquipmentEffectDefinition? Parse(ItemTemplate item, PvfToken[] tokens, Func<int, string> resolve,
        Func<int, bool> effectExists)
    {
        if (item.Kind != ItemKind.Consumable || item.UseForbidden) return null;
        var document = new PvfTokenDocument(tokens, resolve);
        var types = document.Sections.Where(section => section.Name == "stackable type").ToArray();
        var values = document.Sections.Where(section => section.Name == "int data").ToArray();
        if (!document.Diagnostics.IsEmpty || types is not [{ Children.Length: 0, Values.Length: 2 } type] ||
            type.Values[0].Type != 6 || resolve(type.Values[0].Value) != "[equipment effect]" ||
            type.Values[1] is not { Type: 0, Value: 0 } ||
            values is not [{ Children.Length: 0, Values.Length: 1 } data] ||
            data.Values[0] is not { Type: 0, Value: >= 0 and <= ushort.MaxValue } value ||
            value.Value != 0 && !effectExists(value.Value)) return null;
        return new(item, (ushort)value.Value);
    }
}

internal sealed partial class ItemCatalog
{
    readonly ConcurrentDictionary<int, Lazy<EquipmentEffectDefinition?>> equipmentEffects = new();

    public EquipmentEffectDefinition? EquipmentEffect(int id)
    {
        if (!stackable.TryGetValue(id, out var path)) return null;
        return equipmentEffects.GetOrAdd(id, key => new(() => EquipmentEffectDefinition.Parse(Get(key),
            pvf.ReadTokens("stackable/" + path), pvf.ResolveString, effect =>
            {
                var effects = PvfScript.List(pvf.ReadText("etc/equipmenteffect/equipmenteffect.lst"));
                return effects.TryGetValue(effect, out var effectPath) && pvf.Contains("etc/equipmenteffect/" + effectPath);
            }))).Value;
    }
}
