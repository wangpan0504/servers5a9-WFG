using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

internal sealed record RuneEquipRequest(ushort TargetSlot, ushort SourceSlot, int ItemId);
internal sealed record TalismanRefineRequest(short MaterialSlot, short TalismanSlot);
internal sealed record TalismanCompoundRequest(byte Family, byte Mode, int Rule,
    ImmutableArray<short> SourceSlots);
