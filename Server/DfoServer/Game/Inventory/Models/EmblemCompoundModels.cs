using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

internal readonly record struct EmblemCompoundInput(int ItemId, short Slot)
{
    public InventorySlot Position => new(InventoryListType.Main, Slot);
}

internal sealed record EmblemCompoundRequest(ImmutableArray<EmblemCompoundInput> Inputs);

internal sealed record EmblemCompoundReceipt(int BoosterItemId, int MinimumGrade, int InputCount,
    int RewardItemId, int RewardCount, bool PlatinumReroll);
