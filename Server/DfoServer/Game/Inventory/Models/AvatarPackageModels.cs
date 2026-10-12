namespace DfoServer.Game.Inventory.Models;

internal sealed record AvatarPackageChoice(int ItemId, byte Ability);
internal sealed record AvatarPackageOpenRequest(short Slot, int Count, byte Category, byte Subcategory,
    int[] ItemIds, AvatarPackageChoice[] Choices);
