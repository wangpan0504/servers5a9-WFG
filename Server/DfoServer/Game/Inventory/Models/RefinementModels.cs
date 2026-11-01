namespace DfoServer.Game.Inventory.Models;

internal sealed record RefinementRequest(InventorySlot Position, int ItemId, short MaterialSlot, byte Confirmation);
internal sealed record RefinementReceipt(short MaterialSlot, int MaterialRemaining, byte OldLevel, bool Succeeded,
    byte NewLevel, InventorySlot Position)
{
    public required InventoryItem Target { get; init; }
    public int SourceItemId { get; init; }
    public bool UsedTicket { get; init; }
    public bool Portable { get; init; }
    public bool NeedsAttributeRefresh => Position.List == InventoryListType.Equipment && OldLevel != NewLevel;
    public bool UsesLevelProbability => !UsedTicket || Portable;
    public bool Announce => NewLevel >= 6 && UsesLevelProbability;
}
