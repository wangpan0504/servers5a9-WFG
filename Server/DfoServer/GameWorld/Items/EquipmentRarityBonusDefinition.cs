using System.Collections.Frozen;

namespace DfoServer.GameWorld.Items;

internal readonly record struct EquipmentRarityBonus(int RarePercent, int UniquePercent)
{
    public bool IsEmpty => RarePercent == 0 && UniquePercent == 0;
    public EquipmentRarityBonus Add(EquipmentRarityBonus other) =>
        new(checked(RarePercent + other.RarePercent), checked(UniquePercent + other.UniquePercent));
}

// 当前资源只有效果说明，没有通用掉率词条；按已核对的装备和套装编号登记业务。
internal readonly record struct EquipmentRarityBonusDefinition(byte Slot, EquipmentRarityBonus Bonus)
{
    static readonly (int Set, int Necklace, int Bracelet, int Ring)[] Families =
    [
        (11090, 100300068, 100310062, 100320069), (11094, 100300072, 100312031, 100321879),
        (11096, 100300074, 100310068, 100320075), (11113, 100300146, 100310131, 100320148),
        (11151, 20171, 22163, 24192), (11152, 20172, 22164, 24193),
        (11153, 20173, 22165, 24194), (11154, 20174, 22166, 24195),
        (11155, 20175, 22167, 24196), (11156, 20176, 22168, 24197),
        (11157, 20177, 22169, 24198), (11158, 20178, 22170, 24199),
        (11169, 20259, 22251, 24281), (12529, 100300600, 100312295, 100322163),
        (12606, 100300752, 100312444, 100322330)
    ];
    static readonly FrozenDictionary<int, EquipmentRarityBonusDefinition> Definitions = Build();
    public static IReadOnlyDictionary<int, EquipmentRarityBonusDefinition> Items => Definitions;
    public static EquipmentRarityBonusDefinition? Get(int itemId) =>
        Definitions.TryGetValue(itemId, out var value) ? value : null;

    public static EquipmentRarityBonus SetBonus(int setId) =>
        setId == 11094 ? new(0, 6) : setId == 31000 || Families.Any(row => row.Set == setId) ? new(5, 2) : default;

    static FrozenDictionary<int, EquipmentRarityBonusDefinition> Build()
    {
        var result = new Dictionary<int, EquipmentRarityBonusDefinition>();
        foreach (var row in Families)
        {
            result.Add(row.Necklace, new(19, new(6, 3)));
            result.Add(row.Bracelet, new(20, new(6, 3)));
            result.Add(row.Ring, new(21, new(6, 3)));
        }
        foreach (int id in new[] { 400300025, 400300036 }) result.Add(id, new(19, new(6, 3)));
        result.Add(400310028, new(20, new(6, 3)));
        result.Add(400320024, new(21, new(6, 3)));
        result.Add(24191, new(21, new(3, 0)));
        result.Add(100320775, new(21, new(5, 0)));
        // 100331150.equ 在名册中的真实编号是400310002，不按文件名猜编号。
        foreach (int id in new[] { 100310478, 100310690, 400310002, 400990040 }) result.Add(id, new(20, new(50, 30)));
        result.Add(100341606, new(22, new(0, 5)));
        result.Add(400330027, new(13, new(0, 200)));
        foreach (int id in new[] { 400360003, 400360004, 400360005 }) result.Add(id, new(31, new(0, 300)));
        return result.ToFrozenDictionary();
    }
}
