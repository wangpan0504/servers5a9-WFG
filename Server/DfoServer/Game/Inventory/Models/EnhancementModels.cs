using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Models;

// CMD80保留完整的界面文字与三个额外槽字段；文字不作为身份、费用或目标资格。
internal sealed record EnhancementRequest(EnhancementMode Mode, InventorySlot Target, int TargetItemId,
    short MaterialSlot, short ProtectionSlot, ReadOnlyMemory<byte> TargetText, short AuxiliarySlot0, short AuxiliarySlot1);

internal sealed record EnhancementReceipt(EnhancementMode Mode, InventorySlot Target, short MaterialSlot,
    int MaterialRemaining, byte OldLevel, byte NewLevel, bool Succeeded);
