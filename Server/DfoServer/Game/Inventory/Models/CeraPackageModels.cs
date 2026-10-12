namespace DfoServer.Game.Inventory.Models;

// OPEN_CERAPACKAGE（518）携带源槽位和可选时装选择列表。
// 当前固定奖励入口只接受空列表，每次开启一个礼包；非空列表留给独立时装合同。
internal sealed record CeraPackageOpenRequest(short Slot, CeraPackageChoice[] Choices);
internal sealed record CeraPackageChoice(int ItemId, byte Option);
