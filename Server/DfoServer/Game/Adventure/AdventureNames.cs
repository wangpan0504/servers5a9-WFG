using System.Text;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Adventure;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Adventure;

internal static class AdventureName
{
    public static bool IsValid(string name)
    {
        if (name.Length is < 1 or > 16 || name.Any(value => !char.IsLetterOrDigit(value) && value != '_')) return false;
        try { return CharacterName.Encoding.GetByteCount(name) <= 32; }
        catch (EncoderFallbackException) { return false; }
    }
}

internal sealed record AdventureNamePlan(AdventureSnapshot Before, AdventureState After, Character? Expected, InventoryPlan? Inventory);
internal sealed record AdventureNameCommit(bool Accepted, ushort Error, AdventureSnapshot? Snapshot = null, Character? Character = null);
internal sealed record AdventureNameResult(bool Accepted, ushort Error, AdventureSnapshot? Snapshot = null, InventoryResult? Inventory = null)
{
    public bool Changed { get; init; }
    public Character? Selected { get; init; }
    public long Generation { get; init; }
    public long Epoch { get; init; }
    public SessionPhase Phase { get; init; }
}

internal sealed class AdventureNameService(AdventureCatalog catalog, Func<int, AdventureNameTicketDefinition?> ticket)
{
    public AdventureCatalog Catalog => catalog;
    public (AdventureNamePlan? Plan, ushort Error) Prepare(AdventureSnapshot snapshot, Character? character, string name,
        bool initial, ushort slot, ushort mode, DateTimeOffset now)
    {
        if (!AdventureName.IsValid(name)) return (null, initial ? (ushort)3 : (ushort)19);
        if (initial && snapshot.State.NameInitialized && name != snapshot.State.Name) return (null, 3);
        if (!initial && (character is null || mode != 0)) return (null, 19);
        var after = snapshot.State with { Name = name, NameInitialized = true };
        InventoryPlan? inventory = null;
        if (!initial)
        {
            if (slot > short.MaxValue) return (null, 2);
            var source = character!.Inventory.Find(new(InventoryListType.Main, (short)slot));
            if (source is null || source.Core.Kind != ItemKind.Consumable || source.Core.Count <= 0 ||
                source.Core.EquipmentLockId != 0 || source.IsExpired(now.ToUnixTimeSeconds())) return (null, 2);
            var definition = ticket(source.Core.ItemId);
            if (definition is null || definition.TemplateId != source.Core.ItemId || !definition.Allows(character.Level, character.Job)) return (null, 2);
            if (after == snapshot.State) return (new(snapshot, after, character, null), 0);
            var consumed = InventoryDeleteService.Plan(character.Inventory, source.Position, source.Core.Value, 1);
            if (!consumed.Accepted) return (null, 2);
            inventory = new(character.Inventory, consumed.Plan!.After, "adventure_name_change");
        }
        return (new(snapshot, after, character, inventory), 0);
    }
}
