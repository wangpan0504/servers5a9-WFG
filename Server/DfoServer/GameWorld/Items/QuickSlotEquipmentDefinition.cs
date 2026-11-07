using PvfLib;

namespace DfoServer.GameWorld.Items;

// ARAD 类型31是快捷栏符咒/纹章，不是护石的第二个穿戴槽。
internal sealed record QuickSlotEquipmentDefinition(bool Heraldry)
{
    public const byte EquipmentType = 31;

    // 能量复用ItemTemplate.Durability；0是永久型，不另从负重字段建立第二套上限。
    public static QuickSlotEquipmentDefinition Parse(PvfDocument script) => new(script.Has("heraldry item"));
}
