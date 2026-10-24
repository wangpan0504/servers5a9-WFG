using System.Text.Json.Serialization;

namespace DfoServer.Game.Inventory.Models;

internal sealed record LimitCubeRequest(short TargetSlot, int TargetItemId, short TicketSlot)
{
    public InventorySlot Target => new(InventoryListType.Main, TargetSlot);
    public InventorySlot Ticket => new(InventoryListType.Main, TicketSlot);
}

internal sealed record LimitCubeReceipt(int TicketItemId, int TargetItemId, int ResultItemId,
    ushort ResultCount, InventorySlot ResultSlot, long Roll, long TotalWeight)
{
    public AttributePairSelectionSummary? SelectionSummary { get; init; }
    [JsonIgnore] public byte[]? SelectionTrace { get; init; }
}
