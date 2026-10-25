using System.Collections.Immutable;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Models;

// 请求中的材料引用是 list38 槽位（勋章 0..48、守护珠 49..97），不是物品 ID。
internal sealed record GuildCompoundRequest(int Grade, ImmutableArray<int> Slots)
{
    public int Count => Slots.Length;
    public InventorySlot Position(int slot) => new(InventoryListType.GuildMedal, checked((short)slot));
}

// Only accepted compounds have a receipt and a nonzero product; rejection has no plan.
internal sealed record GuildCompoundReceipt(GuildCompoundKind Kind, int InputGrade, int InputCount, int NextGrade,
    int SuccessRate, bool Success, int ProductItemId);
