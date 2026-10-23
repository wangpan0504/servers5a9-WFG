using DfoServer.GameWorld.Items;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class RecipeResourceTests
{
    const int Blueprint = 10304473;

    public static void Run(PvfArchive pvf)
    {
        int checks = Count;
        var catalog = new ItemRecipeCatalog(pvf, PvfScript.List(pvf.ReadText("stackable/stackable.lst")));
        var definition = catalog.Get(Blueprint);
        That(definition is not null, "黑暗祭礼设计图可解析");
        if (definition is null)
        {
            Console.WriteLine($"PASS {Count - checks} 配方资源断言");
            return;
        }
        That(definition.Inputs[0] == new ItemMaterialCost(100390005, 1) && definition.MaintainAbility == "normal" && definition.MaintainSocketStat,
            "装备升级配方显式保留原件属性，第一输入仍完整保存");
        That(definition.Outputs is [{ ItemId: 100390011, Count: 1 }], "产物唯一");
        That(definition.Inputs.Length == 4 && definition.Inputs.Contains(new ItemMaterialCost(10159623, 40)) &&
            definition.Inputs.Contains(new ItemMaterialCost(10158964, 5)) &&
            definition.Inputs.Contains(new ItemMaterialCost(3262, 100)), "固定材料数量正确，商店need material不混入制作费用");
        That(catalog.Get(10087066) is { CatalogDirect: false, MakingType: 2, MakingLevel: 1, Inputs.Length: 5,
            Outputs: [{ ItemId: 100060157, Count: 1 }] } recipe && recipe.Inputs[0] == new ItemMaterialCost(3037, 36),
            "魔法封印皮护甲设计图完整读取五种纯材料及leather熟练度");
        That(catalog.Get(5058) is { CatalogDirect: true, MakingType: null, Inputs: [{ ItemId: 3030, Count: 1 }, { ItemId: 0, Count: 200 }],
            Outputs: [{ ItemId: 3135, Count: 1 }] }, "罗杰普通布片设计图允许NPC模式，编号0是金币");
        var making = new ItemMakingSkillCatalog(pvf);
        That(making.Available && making.ExperienceBase == 1 && making.ExperienceRange == 3 && making.RecipeLevelGap == 2 &&
            making.Level(0, 14) == 1 && making.Level(20, 20) == 2, "当前资源提供1至3熟练经验、等级差2限制及逐级阈值");
        // 非[recipe]物品不能当配方使用。
        That(catalog.Get(10308046) is null, "设计图升级类物品不按普通配方解析");
        Console.WriteLine($"PASS {Count - checks} 配方资源断言");
    }
}
