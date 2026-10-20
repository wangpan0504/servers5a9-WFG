namespace DfoServer.Game.Inventory.Models;

// 1546：主背包槽位 + 目标材质。材质编号与 etc/equipmenttexturechange.etc 的输出顺序一致：
// 0 布甲 / 1 皮甲 / 2 轻甲 / 3 重甲 / 4 板甲。
internal readonly record struct EquipmentTextureChangeRequest(short Slot, byte Material);
