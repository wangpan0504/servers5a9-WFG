using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

internal sealed record AvatarCompoundRequest(short MaterialSlot,
    short SourceSlot0, int SourceItemId0, short SourceSlot1, int SourceItemId1,
    int TargetItemId, ushort Ability, byte TargetIsRare, byte MaterialType)
{
    public InventorySlot Material => new(InventoryListType.Main, MaterialSlot);
    public InventorySlot Source0 => new(InventoryListType.Avatar, SourceSlot0);
    public InventorySlot Source1 => new(InventoryListType.Avatar, SourceSlot1);
}

internal sealed record AvatarCompoundOutput(InventorySlot Position, int ItemId);

internal sealed record AvatarCompoundReceipt(int MaterialItemId, byte MaterialType, string Part,
    int RareRate, int RareRoll, bool HitRare, ImmutableArray<AvatarCompoundOutput> Outputs);
