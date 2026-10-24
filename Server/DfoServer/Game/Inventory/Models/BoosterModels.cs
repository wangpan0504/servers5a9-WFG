using System.Text.Json.Serialization;

namespace DfoServer.Game.Inventory.Models;

internal sealed record BoosterOpenRequest(short Slot, int Count, BoosterSelectionRequest? Selection = null);
internal sealed record BoosterSelectionRequest(byte Category, byte Subcategory, int[] ItemIds);
internal sealed record BoosterGrantedItem(int ItemId, int Count);
internal sealed record LotteryOpenRequest(ushort Mode, short Slot);
internal sealed record LotteryReceipt(short SourceSlot, InventorySlot RewardSlot, int ItemId, int Count);
internal sealed record BoosterReceipt(int ItemId, short Slot, BoosterGrantedItem[] Rewards)
{
    public AttributePairSelectionSummary? SelectionSummary { get; init; }
    [JsonIgnore] public byte[]? SelectionTrace { get; init; }
    // 本版单帧body最多65520B；18B头部后每项占8B，提交前必须可完整交付。
    public const int MaximumItems = (65520 - 18) / 8;
}

internal sealed record AttributePairSelectionSummary(string SelectionMode, string PolicyIdentity, int BeadCount,
    int Bound, int DrawCalls, int TraceBytes, string TraceSha256);
