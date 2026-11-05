using DfoServer.Game.BattlePass;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.BattlePass;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassColorlessSkillTests
{
    public static void RunCore()
    {
        int before = Count;
        var mission = new BattlePassMission(15, 5, 100, 15, "使用无色小晶块技能", 3037);
        That(BattlePassColorlessSkillSource.Matches(mission), "只接入日任务15：无色技能5次");
        foreach (var other in new[] { mission with { Id = 4 }, mission with { RawTypeCode = 3 }, mission with { RawParameter = -1 },
            mission with { RequiredCount = 1 }, mission with { Experience = 1000 } })
            That(!BattlePassColorlessSkillSource.Matches(other), "其他消耗或同物品定义不泛接无色技能");
        short slot = ItemSlotBoundService.CurrencySlot(CurrencyKind.ClearCube)!.Value;
        var wallet = InventorySnapshot.Empty with { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.ClearCube, 20) };
        var request = new InventoryDeleteRequest(InventoryListType.Main, [new(2, slot, 3037, 5)], 0);
        var spent = InventoryDeleteService.PlanBatch(wallet, request);
        var plan = spent.Plan!;
        var fact = plan.ColorlessSkill!;
        That(spent.Accepted && fact.Spent == 5 && fact.Matches(plan, plan.After) &&
            plan.After.AccountWallet.Get(CurrencyKind.ClearCube) == 15, "一次施法扣5个无色只记1次来源，进度按次而不是按个数");
        That(ReferenceEquals(plan.WithAfter(plan.After).ColorlessSkill, fact), "库存计划复制保留同一次无色技能事实");
        var again = InventoryDeleteService.PlanBatch(plan.After, request with { Items = [new(2, slot, 3037, 1)] });
        That(again.Plan!.ColorlessSkill is { Spent: 1 } next && next.Id != fact.Id, "下一次施法是新的来源");
        var practice = InventoryDeleteService.PlanBatch(wallet, request, preserveMaterials: true);
        That(practice.Accepted && practice.Plan!.ColorlessSkill is null && !practice.Plan.Changed, "修炼场免耗不记无色技能");
        var script = request with { Items = [new(3, slot, 3037, 1)] };
        var scripted = InventoryDeleteService.PlanBatch(wallet, script);
        That(scripted.Accepted && scripted.Plan!.Reason == "delete_equipment_material" && scripted.Plan.ColorlessSkill is null,
            "装备脚本扣无色不冒充技能施法");
        var colored = request with { Items = [new(2, (short)(slot - 1), 3036, 1)] };
        var blue = InventoryDeleteService.PlanBatch(wallet with { AccountWallet = wallet.AccountWallet.Set(CurrencyKind.BlueCube, 3) }, colored);
        That(blue.Accepted && blue.Plan!.ColorlessSkill is null, "有色晶体技能不推进无色任务");
        That(!(fact with { Id = Guid.Empty }).Matches(plan, plan.After) &&
            !(fact with { Spent = 4 }).Matches(plan, plan.After), "空来源或与实际扣款不符不计进度");
        var quickRebound = new BattlePassMission(5, 1, 100, 4, "使用[受身蹲伏]技能", 190);
        That(BattlePassQuickReboundSource.Matches(quickRebound) &&
            !BattlePassQuickReboundSource.Matches(quickRebound with { Id = 4 }) &&
            !BattlePassQuickReboundSource.Matches(quickRebound with { RawTypeCode = 15 }) &&
            !BattlePassQuickReboundSource.Matches(quickRebound with { RawParameter = 191 }) &&
            !BattlePassQuickReboundSource.Matches(quickRebound with { RequiredCount = 2 }) &&
            !BattlePassQuickReboundSource.Matches(quickRebound with { Experience = 1000 }),
            "受身蹲伏任务只匹配日任务5：raw4、技能190、需求1、100经验");
        var dailyExp = new BattlePassMission(14, 3_000_000, 100, 14, "获得经验值", -1);
        var weeklyExp = new BattlePassMission(27, 10_000_000, 1000, 14, "获得经验值", -1);
        var seasonExp = new BattlePassMission(52, 2_000_000_000, 1600, 14, "获得经验值", -1);
        That(BattlePassExperienceGainSource.Matches(0, dailyExp) &&
            BattlePassExperienceGainSource.Matches(1, weeklyExp) &&
            BattlePassExperienceGainSource.Matches(2, seasonExp) &&
            !BattlePassExperienceGainSource.Matches(1, dailyExp) &&
            !BattlePassExperienceGainSource.Matches(0, weeklyExp) &&
            !BattlePassExperienceGainSource.Matches(0, seasonExp) &&
            !BattlePassExperienceGainSource.Matches(0, dailyExp with { RawParameter = 0 }) &&
            !BattlePassExperienceGainSource.Matches(0, dailyExp with { RawTypeCode = 13 }),
            "角色经验获得来源按类别精确匹配日14、周27与季52");
        Console.WriteLine($"PASS BattlePassColorlessSkillTests core ({Count - before} checks)");
    }
}
