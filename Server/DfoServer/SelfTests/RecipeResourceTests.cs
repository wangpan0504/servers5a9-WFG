using DfoServer.GameWorld.Items;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 真实 PVF 的 [recipe] 设计图：黑暗祭礼（10304473）把 100390005 改成 100390011，
// 材料 10159623×40、10158964×5、3262×100，产物唯一。
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
        That(definition.TargetItemId == 100390005, "第一对被改造装备");
        That(definition.OutputItemId == 100390011 && definition.OutputCount == 1, "产物唯一");
        That(definition.Materials.Length == 3 && definition.Materials.Contains(new ItemMaterialCost(10159623, 40)) &&
            definition.Materials.Contains(new ItemMaterialCost(10158964, 5)) &&
            definition.Materials.Contains(new ItemMaterialCost(3262, 100)), "固定材料数量正确");
        // 非[recipe]物品不能当配方使用。
        That(catalog.Get(10308046) is null, "设计图升级类物品不按普通配方解析");
        Console.WriteLine($"PASS {Count - checks} 配方资源断言");
    }
}
