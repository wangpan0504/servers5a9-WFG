using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

internal readonly record struct MakeWeaponSkinRequest(byte Type, int Recipe, InventorySlot Source);
internal readonly record struct SelectSkinRequest(int Type, int SkinId)
{
    public ImmutableArray<int> AdditionalIds { get; init; } = [];
    public IEnumerable<int> Ids => new[] { SkinId }.Concat(AdditionalIds);
}
internal readonly record struct SkinStorageKey(byte Type, int SkinId);
