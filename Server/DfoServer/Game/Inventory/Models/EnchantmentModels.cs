namespace DfoServer.Game.Inventory.Models;

// CMD272独立宝珠入口；末尾字段保持原始值，不能当作费用或附魔资格。
internal sealed record EnchantmentRequest(InventorySlot Source, InventorySlot Target, uint Opaque);
