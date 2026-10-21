using DfoServer.GameWorld.Items;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 设计图升级规则全部来自真实 PVF：断言解析出的组数、可变装备槽、固定材料与产物，
// 以及「同一组的多件传说共用 0 号位」这一关键结构。
internal static class UpgradeChronicleTests
{
    public static void Run(PvfArchive pvf)
    {
        int checks = Count;
        var catalog = new UpgradeChronicleCatalog(pvf, PvfScript.List(pvf.ReadText("stackable/stackable.lst")));
        var definition = catalog.Get(10308046);
        That(definition is not null && definition.Recipes.Length == 5, "兵法之神设计图解析出5组");
        if (definition is null)
        {
            Console.WriteLine($"PASS {Count - checks} 设计图升级配方断言");
            return;
        }
        That(definition.Recipes.Length == 5, "5组配方");
        var first = definition.Recipes[0];
        That(first.Group == 0 && first.OutputItemId == 116602299 && first.Accepts.Contains(100090439), "0组产物与可变装备");
        That(first.Materials.Length == 3 && first.Materials.Contains(new ItemMaterialCost(3262, 100)) &&
            first.Materials.Contains(new ItemMaterialCost(3137, 20)) && first.Materials.Contains(new ItemMaterialCost(3155, 10)),
            "0组固定材料数量正确");
        // 设计图的 [need material] 是 NPC 卖价（异形结晶），不能混进升级材料。
        That(first.Materials.All(cost => cost.ItemId != 10305892), "NPC卖价不计入升级材料");
        That(definition.Recipes[4].OutputItemId == 116602303 &&
            definition.Recipes.Select(recipe => recipe.OutputItemId).Distinct().Count() == 5, "5组产物互不相同");
        var shared = definition.Find(100050801);
        That(shared is not null && shared.OutputItemId == 116602299, "同一组的多件传说共用0号位");
        That(definition.Find(101040485) is null, "非本设计图装备不匹配");
        var sibling = catalog.Get(10305608);
        That(sibling is not null && sibling.Recipes.Length == 5 &&
            sibling.Recipes[0].Materials.Contains(new ItemMaterialCost(3160, 20)), "兄弟设计图材料不同且可解析");
        That(catalog.Get(10308047) is null && catalog.Get(0) is null, "非设计图不产出配方");
        Console.WriteLine($"PASS {Count - checks} 设计图升级配方断言");
    }
}
