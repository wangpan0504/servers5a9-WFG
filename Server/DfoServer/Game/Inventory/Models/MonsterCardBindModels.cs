using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

internal readonly record struct MonsterCardBindReward(short Slot, int ItemId, int Value, byte Extra);

internal sealed record MonsterCardBindRequest(short SynthesizerSlot, ImmutableArray<short> CardSlots)
{
    public InventorySlot SynthesizerPosition => new(InventoryListType.Main, SynthesizerSlot);
}

internal sealed record MonsterCardBindReceipt(short SynthesizerSlot, int BindType, int InputRarity, int OutputRarity,
    bool RarityUpgraded, ImmutableArray<short> DeletedSlots, ImmutableArray<MonsterCardBindReward> Rewards);
