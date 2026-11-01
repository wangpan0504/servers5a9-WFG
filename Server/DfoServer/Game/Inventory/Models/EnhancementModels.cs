using DfoServer.GameWorld.Items;
using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

// CMD80保留完整的界面文字与三个额外槽字段；文字不作为身份、费用或目标资格。
internal sealed record EnhancementRequest(EnhancementMode Mode, InventorySlot Target, int TargetItemId,
    short MaterialSlot, short ProtectionSlot, ReadOnlyMemory<byte> TargetText, short AuxiliarySlot0, short AuxiliarySlot1);

internal sealed record EnhancementReceipt(EnhancementMode Mode, InventorySlot Target, short MaterialSlot,
    int MaterialRemaining, byte OldLevel, byte NewLevel, bool Succeeded)
{
    public byte? OutcomeCode { get; init; }
    public byte Path { get; init; }
    public int GoldCost { get; init; }
    public short TerminalSlot { get; init; } = -1;
    public int TerminalRemaining { get; init; }
    public bool InPlace { get; init; }
    // 仅普通逐级升级表路径置位；定级/增量券及随机、幸运奖池不属于该路径。
    public bool UsesLevelProbability { get; init; }
    public ImmutableArray<EnhancementRefund> Refunds { get; init; } = [];
}
internal readonly record struct EnhancementRefund(short Slot, int ItemId, int Count);
