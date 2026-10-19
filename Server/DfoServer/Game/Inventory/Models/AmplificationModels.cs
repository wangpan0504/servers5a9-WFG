using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Models;

internal sealed record AmplificationRequest(AmplificationOperation Operation, short TargetSlot, int TargetItemId,
    short MaterialSlot, int MaterialItemId, byte SelectedOption = 0, ReadOnlyMemory<byte> TargetText = default);

internal sealed record AmplificationReceipt(AmplificationOperation Operation, short MaterialSlot, int MaterialRemaining,
    short TargetSlot, byte Type, ushort Value, byte Level, bool RefreshTarget);
