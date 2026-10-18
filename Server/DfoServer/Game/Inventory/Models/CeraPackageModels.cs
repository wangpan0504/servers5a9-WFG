namespace DfoServer.Game.Inventory.Models;

// OPEN_CERAPACKAGE（518）携带源槽位和可选时装选择列表。
// 空列表发放全部固定奖励；非空列表必须等于 [package data] 中全部时装编号，option 写入 Durability。
internal sealed record CeraPackageOpenRequest(short Slot, CeraPackageChoice[] Choices);
internal sealed record CeraPackageChoice(int ItemId, byte Option);
