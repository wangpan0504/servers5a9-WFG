namespace DfoServer.GameWorld.Items;

// 这些装备没有金币效果词条：旧端按编号识别，当前PVF只在说明中声明比例。
// 仅登记已核对的装备，不从名称、说明或掉落概率标签推导其他物品的效果。
internal readonly record struct EquipmentGoldBonusDefinition(ItemKind Kind, byte Slot, int Percent)
{
    public static EquipmentGoldBonusDefinition? Get(int itemId) => itemId switch
    {
        26115 or 26341 or 26626 or 100331319 => new(ItemKind.Equipment, 13, 3),
        26342 or 26627 => new(ItemKind.Equipment, 13, 4),
        100331240 => new(ItemKind.Equipment, 13, 10),
        24191 => new(ItemKind.Equipment, 21, 10),
        100320775 => new(ItemKind.Equipment, 21, 12),
        100341606 => new(ItemKind.Equipment, 22, 30),
        102010249 or 104000181 or 101020286 or 101020526 or 109000133 => new(ItemKind.Equipment, 12, 3),
        2747548 => new(ItemKind.Creature, 26, 5),
        _ => null
    };
}
