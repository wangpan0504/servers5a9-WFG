using DfoServer.GameWorld.Items;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class TalismanResourceTests
{
    public static void Run(PvfArchive pvf, ItemCatalog items)
    {
        int start = Count;
        var stones = PvfScript.List(pvf.ReadText("equipment/equipment.lst"))
            .Where(row => row.Value.StartsWith("character/common/talisman/", StringComparison.OrdinalIgnoreCase)).ToArray();
        var runes = PvfScript.List(pvf.ReadText("stackable/stackable.lst"))
            .Where(row => row.Value.StartsWith("rune/", StringComparison.OrdinalIgnoreCase)).ToArray();
        That(stones.Length > 0 && runes.Length > 0, "护石和符文资源名册非空");
        int placeholders = 0;
        foreach (var row in stones)
        {
            var definition = items.Talisman(row.Key);
            That(definition is not null && definition.Item.Id == row.Key, "名册护石可解析为实际模板");
            if (definition!.IsPlaceholder)
            {
                placeholders++;
                That(!definition.AllowsUse(255, 0, 1), "占位护石不可装备");
            }
            else
            {
                That(definition.SkillIndex > 0 && !definition.FirstGrowTypes.IsEmpty,
                    "有效护石保留技能及首转资格");
            }
            That(ReferenceEquals(definition, items.Talisman(row.Key)), "护石重复查询复用同一不可变定义");
        }
        var camps = new HashSet<RuneCamp>();
        foreach (var row in runes)
        {
            var definition = items.Rune(row.Key);
            That(definition is not null && definition.Item.Id == row.Key && definition.SkillIndex > 0 &&
                !definition.FirstGrowTypes.IsEmpty, "名册符文保留实际模板、技能及首转资格");
            camps.Add(definition!.Camp);
            That(ReferenceEquals(definition, items.Rune(row.Key)), "符文重复查询复用同一不可变定义");
        }
        That(placeholders > 0 && camps.Count == Enum.GetValues<RuneCamp>().Length,
            "实际资源包含占位护石和五种符文阵营");
        var costs = items.TalismanRefineCosts();
        That(costs.MaterialId > 0 && items.Contains(costs.MaterialId) && costs.CountsByRarity.Count > 0 &&
            costs.CountsByRarity.Values.All(count => count > 0), "重锻材料真实存在且数量表完整");
        That(items.Talisman(-1) is null && items.Rune(-1) is null && items.Talisman(runes[0].Key) is null &&
            items.Rune(stones[0].Key) is null, "未知编号与交叉类别不生成错误定义");
        Console.WriteLine($"PASS {Count - start} talisman resource checks stones={stones.Length} runes={runes.Length} placeholders={placeholders}");
    }
}
