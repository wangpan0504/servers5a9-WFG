using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.CollectBox;

internal readonly record struct CollectBoxAddRequest(byte Box, ushort SourceSlot, int ItemId);
internal readonly record struct CollectBoxRemoveRequest(byte Box, int ItemId);
internal readonly record struct CollectBoxExtendRequest(byte Box, ushort SourceSlot, int ItemId);

internal sealed record CollectBoxPlan(Character Before, CollectBoxState After, InventoryPlan Inventory)
{
    public bool Changed => Inventory.Changed || !Before.CollectBox.Same(After);
}

internal sealed record CollectBoxResult(bool Accepted, ushort Error, InventoryResult Inventory, CollectBoxPlan? Plan)
{
    public CollectBoxCommitReceipt? Receipt { get; init; }

    public static CollectBoxResult Reject(ushort error) => new(false, error, InventoryResult.Reject(error), null);
    public static CollectBoxResult Accept(CollectBoxPlan plan) =>
        new(true, 0, new(true, 0, plan.Inventory), plan);
}

internal sealed class CollectBoxCommitReceipt(Character character, long generation, long selectionEpoch)
{
    public Character Character { get; } = character;
    public long Generation { get; } = generation;
    public long SelectionEpoch { get; } = selectionEpoch;
}
