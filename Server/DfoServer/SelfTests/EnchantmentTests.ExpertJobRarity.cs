using System.Collections;
using System.Reflection;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class EnchantmentTests
{
    static void ExpertJobKnownCardRarityQualificationPlans()
    {
        // 全部编号、卡片和配方均为合成输入，不代表381存在副职业配方。
        const int beadId = 990201, cardId = 990202, gearId = 990203, recipeId = 990204, materialId = 990205;
        const string card = """
            [stackable type]
            `[material expert job]` 1
            [string data]
            `synthetic-card.img` `[title name]`
            [/string data]
            [enchant table]
            [enchant index] 0
            [all elemental attack] 15
            [/enchant index]
            [enchant index] 1
            [all elemental attack] 16
            [/enchant index]
            [/enchant table]
            """;
        const string excluded = "\n[unable use rarity]\n`common` `uncommon`\n[/unable use rarity]\n";
        var recipe = new ExpertJobCardRecipe(cardId, beadId, recipeId, 1, 1,
            [(0, 17), (materialId, 2), (3037, 3)], []);
        var scripts = SyntheticExpertJobRarityScripts(recipe);
        var source = new InventoryItem(81, new(InventoryListType.Main, 233), new(cardId, ItemKind.Profession, 2)
            { SortLockFlag = 1 });
        var target = new InventoryItem(82, new(InventoryListType.Main, 9), new(gearId, ItemKind.Equipment, 1234567)
        {
            Attr = 7, GenuineUpgrade = 4, AmplifyType = 2, AmplifyValue = 21, Rune = 8,
            Durability = 12, SealFlag = 1, EnchantCardId = 3175, EnchantUpgrade = 2,
            SortLockFlag = 1, Random0 = new(1, 2, 3)
        });
        var material = new InventoryItem(83, new(InventoryListType.Main, 121), new(materialId, ItemKind.Material, 5));
        Character character = new(1, 0, "ExpertRarity", 0, new(0, 0, 0, 0), null)
        {
            Level = 50, ExpertJobType = 1, ExpertJobRecipes = [recipeId], ExpertJobExperience = 20,
            ExpertMachineEndurance = 300,
            Inventory = new([source, target, material], Gold: 1000)
            { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.ClearCube, 10).Set(CurrencyKind.Cera, 123) }
        };
        var cardTemplate = new ItemTemplate(cardId, ItemKind.Profession, 0, 1, 0, 1000, 1, 1, 1,
            "[free]", ushort.MaxValue, 0, "SyntheticCard");
        var gearTemplate = new ItemTemplate(gearId, ItemKind.Equipment, 13, 1, 45, 1, 20, 0, 1,
            "[free]", ushort.MaxValue, 0, "SyntheticTitle");
        var policy = new ExpertJobCompatibility { EnchantSuccessPercent = 100, EnchantGoldCost = 13, EnchantEnduranceCost = 2 };
        EnchantmentCatalog Catalog(bool restricted) => new(path => path == "stackable/bead.stk"
            ? $"[stackable type]\n`[enchant waste]` 0\n[monster card id]\n{cardId}\n"
            : card + (restricted ? excluded : ""),
            new Dictionary<int, string> { [beadId] = "bead.stk", [cardId] = "card.stk" });

        // 组合只覆盖本次目标稀有度差集；旧资源事务、重开和TCP回归沿用原用例。
        foreach (bool restricted in new[] { true, false })
            foreach (int rarity in new[] { 0, 1, 2, 3, 4, 5, 6 })
                VerifyPlan(restricted, rarity, 0);
        foreach (int rarity in new[] { 0, 1, 2 }) VerifyPlan(true, rarity, 1);
        VerifyPlan(false, 0, 1);

        var beadTemplate = new ItemTemplate(beadId, ItemKind.Consumable, 0, 1, 0, 1000, 1, 1, 1,
            "[free]", ushort.MaxValue, 0, "SyntheticBead");
        var beadItems = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        beadItems.PrefillTemplates(new Dictionary<int, ItemTemplate>
        { [beadId] = beadTemplate, [cardId] = cardTemplate, [gearId] = gearTemplate with { Rarity = 2 } });
        var beadItem = new InventoryItem(84, new(InventoryListType.Main, 82), new(beadId, ItemKind.Consumable, 1));
        var beadRole = character with { Inventory = new([beadItem, target], Gold: 1000) };
        var rangeScripts = SyntheticExpertJobRarityScripts(recipe, new(1, 3, 7));
        int expDraws = 0;
        var beadService = new DfoServer.Game.Inventory.Services.InventoryEnchantmentService(beadItems, Catalog(false));
        That(ExpertJobCraft.TryPlanEnchant(beadRole, beadService, rangeScripts, 0, 9, 0, 82, 2_000_000_000,
            out var beadPlan, out int beadExp, out _, upper => { That(upper == 5, "宝珠附魔按MonsterCardExp的min..max区间采样"); expDraws++; return 4; }) &&
            expDraws == 1 && beadExp == 27 && beadPlan.After.Find(target.Position)!.Core.EnchantCardId == cardId,
            "TryPlanEnchant将next委托传入AddCardExp，按[monstercard exp]区间计算熟练度而非固定取min");

        void VerifyPlan(bool restricted, int rarity, byte upgrade)
        {
            var sourceCard = source with { Core = source.Core with { EnchantUpgrade = upgrade } };
            var role = character with { Inventory = character.Inventory with
            { Items = character.Inventory.Items.Replace(source, sourceCard) } };
            string before = JsonSerializer.Serialize(role);
            var items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
            items.PrefillTemplates(new Dictionary<int, ItemTemplate>
            { [cardId] = cardTemplate, [gearId] = gearTemplate with { Rarity = rarity } });
            int rolls = 0;
            var enchanting = new ExpertJobEnchanting(scripts, items, Catalog(restricted), policy,
                random: upper => { That(upper == 100, "固定经验合成配方只抽一次附魔成功率"); rolls++; return 0; });
            var plan = enchanting.Plan(role, recipeId, target.Position, sourceCard.Position);
            string label = $"合成副职业卡 restricted={restricted} rarity={rarity} upgrade={upgrade}";
            if (restricted && rarity is 0 or 1)
                That(plan is null && rolls == 0, label + "在扣卡/材料/钱包/金币和经验/耐久计划前拒绝，RNG不调用");
            else
            {
                var after = plan?.Inventory.After;
                That(plan is { Succeeded: true, Experience: 27, Endurance: 298, EnchantRarity: 1 } && rolls == 1 &&
                    plan.Expected == role && plan.Inventory.Before == role.Inventory && after is not null && after.Items.Length == 3 &&
                    after.Find(sourceCard.Position) == sourceCard with { Core = sourceCard.Core with { Value = 1 } } &&
                    after.Find(target.Position) == target with { Core = target.Core with { EnchantCardId = cardId, EnchantUpgrade = upgrade } } &&
                    after.Find(material.Position) == material with { Core = material.Core with { Value = 3 } } &&
                    after.Gold == 970 && after.AccountWallet.Same(role.Inventory.AccountWallet.Set(CurrencyKind.ClearCube, 7)),
                    label + "沿原成功路径精确扣一卡/两材料/三无色/30金币，加7经验扣2耐久并保留完整目标与升级等级");
            }
            That(JsonSerializer.Serialize(role) == before, label + "保留原角色、资产、经验及耐久快照");
        }
    }

    static ExpertJobScriptCatalog SyntheticExpertJobRarityScripts(ExpertJobCardRecipe recipe, ExpertJobMonsterCardExp? exp = null)
    {
        // Catalog只有PVF工厂；合成自测在本文件内装配最小只读输入，不新增生产测试API。
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var scriptType = typeof(ExpertJobScriptCatalog).GetNestedType("JobScript", BindingFlags.NonPublic)!;
        object script = Activator.CreateInstance(scriptType, nonPublic: true)!;
        scriptType.GetProperty("MonsterCard")!.SetValue(script, new[] { exp ?? new ExpertJobMonsterCardExp(1, 7, 7) });
        var jobs = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(byte), scriptType))!;
        jobs.Add((byte)1, script);
        var catalog = (ExpertJobScriptCatalog)Activator.CreateInstance(typeof(ExpertJobScriptCatalog), instance,
            binder: null, args: [jobs], culture: null)!;
        ((Dictionary<int, ExpertJobCardRecipe>)typeof(ExpertJobScriptCatalog).GetField("cardRecipes", instance)!
            .GetValue(catalog)!).Add(recipe.CardId, recipe);
        // 统一资格校验同时需要配方声明；保留本例的手动已学输入及完整成功/拒绝断言。
        ((Dictionary<int, ExpertJobLearnableRecipe>)typeof(ExpertJobScriptCatalog).GetField("learnable", instance)!
            .GetValue(catalog)!).Add(recipe.RecipeId, new(recipe.RecipeId, 1, recipe.RequiredLevel, recipe.Rarity));
        return catalog;
    }
}
