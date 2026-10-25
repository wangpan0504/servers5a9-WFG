using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ExpertJobCraftTests
{
    public static void Run(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        var scripts = resources.ExpertJobs;
        var items = resources.Items;
        That(scripts.TryRecipe(1, 2600141, out var recipe) && recipe.ProductId == 2610034 && recipe.ProductCount == 1 &&
            recipe.RequiredLevel == 1 && recipe.Materials.SequenceEqual(new[] { (3033, 10), (2610030, 1) }),
            "黑色魔力制作读取实际input/output，不误读商店购买成本");
        foreach (int id in new[] { 2600141, 2600142, 2600143, 2600144, 2600145 })
            That(scripts.TryRecipe(1, id, out var color) && color.RequiredLevel == 1 && color.Materials.Length == 2,
                "五色魔力产物配方均有完整制作链：" + id);
        That(scripts.TryRecipe(2, 2600401, out var transform) && transform.ProductId == 3033 && transform.ProductCount == 10 &&
            transform.Materials.SequenceEqual(new[] { (3035, 10), (3302, 1) }),
            "共享加载器保留炼金红转黑晶体单次10个产出，不能缩水成1个");
        string root = Path.Combine(Path.GetTempPath(), "s5a9-expert-craft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool passed = false;
        try
        {
            string path = Path.Combine(root, "game.db");
            var database = new SqliteGameDatabase(path);
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("expert-craft", () => { });
            var created = repository.Create(owner, new("CraftTest", towns.Room, towns.InitialReturn) { Level = 50 });
            var grant = InventoryRewardGrantService.TryGrantBatch(created, [new(3033, 30), new(2610030, 3)],
                items, resources.GoldLimits, resources.EpicPieces);
            That(grant.Accepted && grant.Plan is not null, "真实黑色晶体入钱包、魔力结晶入实体背包");
            created = repository.CommitInventory(owner, created, grant.Plan!);
            Sql($"INSERT INTO character_expert_job(character_id,account,job_type,expertness,learned_recipes) VALUES({created.Id},'expert-craft',1,0,'[2600141]')");
            var current = repository.LoadCharacter(owner, 0);
            bool Plan(Character character, ushort count, out ExpertJobCraftPlan plan) => ExpertJobCraft.TryPlan(character,
                scripts, items, resources.GoldLimits, resources.EpicPieces, 2600141, count, ushort.MaxValue,
                limit => limit - 1, DateTimeOffset.UtcNow, out plan, out _);
            That(!Plan(current with { ExpertJobRecipes = ImmutableHashSet<int>.Empty }, 1, out _) &&
                !Plan(current, 0, out _) && !Plan(current, 4, out _) && !Plan(current, 101, out _),
                "未学习、零次数、材料不足与越界批量均不规划");
            var material = current.Inventory.Items.Single(item => item.Core.ItemId == 2610030);
            foreach (var core in new[] { material.Core with { EquipmentLockId = 1 }, material.Core with { ExpireTime = 1 } })
                That(!Plan(current with { Inventory = current.Inventory with { Items = current.Inventory.Items.Replace(material, material with { Core = core }) } }, 1, out _),
                    "锁定和到期的魔力结晶不参与合成");
            That(Plan(current, 3, out var plan) && plan.Experience == 3 && plan.SuccessCount == 3 && plan.FailureCount == 0 &&
                plan.Products.SequenceEqual(new[] { new ExpertJobProduct(2610034, 3) }) &&
                CurrencyWallet.Get(plan.Inventory.After, CurrencyKind.BlackCube) == 0 &&
                plan.Inventory.After.Items.All(item => item.Core.ItemId != 2610030),
                "批量三次精确扣30钱包黑晶体和3魔力结晶，产物与经验按批次计算");
            Sql("CREATE TRIGGER fail_craft BEFORE UPDATE OF expertness ON character_expert_job BEGIN SELECT RAISE(ABORT,'craft failure'); END");
            Reject<SqliteException>(() => repository.CommitExpertJobCraft(owner, current, plan.Inventory, plan.Experience),
                "经验末段失败使材料、钱包和产物整笔回滚");
            var rollback = repository.LoadCharacter(owner, 0);
            That(rollback.Revision == current.Revision && rollback.ExpertJobExperience == 0 &&
                rollback.Inventory.Find(material.Position) == material && CurrencyWallet.Get(rollback.Inventory, CurrencyKind.BlackCube) == 30,
                "失败保留源UID、数量与钱包，不留下产物或半笔提交");
            Sql("DROP TRIGGER fail_craft");
            var saved = repository.CommitExpertJobCraft(owner, current, plan.Inventory, plan.Experience);
            var reopened = new CharacterRepository(path).LoadCharacter(owner, 0);
            That(saved.Revision == current.Revision + 1 && reopened.ExpertJobExperience == 3 &&
                reopened.ExpertMachineEndurance == current.ExpertMachineEndurance &&
                reopened.Inventory.Items.Single(item => item.Core.ItemId == 2610034).Core.Count == 3 &&
                CurrencyWallet.Get(reopened.Inventory, CurrencyKind.BlackCube) == 0,
                "一次revision原子保存产物与经验，重开库保留状态且制作不扣附魔耐久");
            Reject<InvalidOperationException>(() => repository.CommitExpertJobCraft(owner, current, plan.Inventory, plan.Experience),
                "旧制作计划不得重放");
            That(!Plan(reopened, 1, out _), "耗尽后重发请求不会无成本产出");
            That(scripts.TryCardRecipe(3619, out var cardRecipe) && cardRecipe.RecipeId == 10015129 &&
                cardRecipe.BeadId == 2600313 && cardRecipe.BeadMaterials.SequenceEqual(new[] { (2610039, 3) }) &&
                resources.Enchantments.Get(cardRecipe.BeadId)?.CardItemId == 3619,
                "真实白卡3619映射2600313宝珠，额外三份灵魂精髓与普通附魔成本分开");
            var supplies = InventoryRewardGrantService.TryGrantBatch(reopened,
                [new(3619, 2), new(3227, 10), new(3166, 40), new(2610039, 6)], items, resources.GoldLimits, resources.EpicPieces);
            That(supplies.Accepted && supplies.Plan is not null, "宝珠真实全套材料发放");
            repository.CommitInventory(owner, reopened, supplies.Plan!);
            Sql($"UPDATE character_expert_job SET learned_recipes='[2600141,10015129]' WHERE character_id={created.Id}");
            var beadSource = repository.LoadCharacter(owner, 0);
            var card = beadSource.Inventory.Items.Single(item => item.Core.ItemId == 3619);
            bool Bead(Character source, uint id, ushort count, out ExpertJobCraftPlan craft) => ExpertJobCraft.TryPlan(source,
                scripts, items, resources.GoldLimits, resources.EpicPieces, id, count, (ushort)card.Position.Index,
                _ => 0, DateTimeOffset.UtcNow, out craft, out _);
            That(!Bead(beadSource, 0, 1, out _) && !Bead(beadSource, 2600141, 1, out _) &&
                !Bead(beadSource, 10015129, 3, out _) && !Bead(beadSource with { ExpertJobRecipes = [] }, 10015129, 1, out _),
                "无配方、错映射、源卡数量不足与未学习不能以扣卡代替成功");
            foreach (var core in new[] { card.Core with { EquipmentLockId = 1 }, card.Core with { ExpireTime = 1 },
                card.Core with { Attr = 1 }, card.Core with { EnchantUpgrade = 1 } })
                That(!Bead(beadSource with { Inventory = beadSource.Inventory with { Items = beadSource.Inventory.Items.Replace(card, card with { Core = core }) } },
                    10015129, 1, out _), "锁定、到期或非标准实例字段未闭合的源卡不能制珠");
            That(Bead(beadSource with { Inventory = beadSource.Inventory with { Items = beadSource.Inventory.Items.Replace(card,
                card with { Core = card.Core with { Attr = 0x20 } }) } }, 10015129, 1, out _),
                "Attr高三位不是升级级别，不误拒绝正常模板卡片");
            var highCard = scripts.KnownCards(1).First(id => scripts.TryCardRecipe(id, out var candidate) && candidate.RequiredLevel == 9);
            That(scripts.TryCardRecipe(highCard, out var highRecipe), "存在九级卡片制珠配方");
            var highSource = beadSource with { ExpertJobRecipes = beadSource.ExpertJobRecipes.Add(highRecipe.RecipeId),
                Inventory = beadSource.Inventory with { Items = beadSource.Inventory.Items.Replace(card,
                    card with { Core = InventoryRewardGrantService.Create(items.Get(highCard), 2) }) } };
            That(!Bead(highSource, (uint)highRecipe.RecipeId, 1, out _), "已学但Lv1不能制作Lv9卡片宝珠，保留截图等级限制");
            var essence = beadSource.Inventory.Items.Single(item => item.Core.ItemId == 2610039);
            var missing = beadSource with { Inventory = beadSource.Inventory with { Items = beadSource.Inventory.Items.Remove(essence) } };
            That(!Bead(missing, 10015129, 1, out _), "缺少bead item额外材料拒绝且不扣卡");
            That(Bead(beadSource, 10015129, 2, out var beadPlan) && beadPlan.SuccessCount == 2 && beadPlan.FailureCount == 0 &&
                beadPlan.Products.SequenceEqual(new[] { new ExpertJobProduct(2600313, 2) }) &&
                beadPlan.Inventory.After.Items.All(item => item.Core.ItemId is not (3619 or 3227 or 3166 or 2610039)) &&
                beadPlan.Inventory.After.Items.Single(item => item.Core.ItemId == 2600313).Core == InventoryRewardGrantService.Create(items.Get(2600313), 2),
                "两次制作精确扣两卡、两份普通材料和六份额外精髓，真正发放完整PVF宝珠实例");
            Sql("CREATE TRIGGER fail_bead BEFORE UPDATE OF expertness ON character_expert_job BEGIN SELECT RAISE(ABORT,'bead failure'); END");
            Reject<SqliteException>(() => repository.CommitExpertJobCraft(owner, beadSource, beadPlan.Inventory, beadPlan.Experience),
                "宝珠经验失败使全部源卡、材料和成品回滚");
            That(repository.LoadCharacter(owner, 0).Inventory.Find(card.Position) == card &&
                repository.LoadCharacter(owner, 0).Revision == beadSource.Revision, "宝珠失败源卡UID与版本保持不变");
            Sql("DROP TRIGGER fail_bead");
            repository.CommitExpertJobCraft(owner, beadSource, beadPlan.Inventory, beadPlan.Experience);
            var beadSaved = new CharacterRepository(path).LoadCharacter(owner, 0);
            That(beadSaved.Revision == beadSource.Revision + 1 && beadSaved.ExpertJobExperience == beadPlan.Experience &&
                beadSaved.Inventory.Items.Single(item => item.Core.ItemId == 2600313).Core.Count == 2 &&
                !Bead(beadSaved, 10015129, 1, out _), "宝珠重开持久化且耗尽后无法重复领取");
            Reject<InvalidOperationException>(() => repository.CommitExpertJobCraft(owner, beadSource, beadPlan.Inventory, beadPlan.Experience),
                "旧宝珠计划不得重放");
            int levelNineExperience = Enumerable.Range(0, 100000).First(value => scripts.Level(1, value) == 9);
            var failureSupplies = InventoryRewardGrantService.TryGrantBatch(beadSaved,
                new[] { new InventoryReward(highCard, 1) }.Concat(highRecipe.Materials.Concat(highRecipe.BeadMaterials)
                    .Select(row => new InventoryReward(row.ItemId, row.Count))).ToArray(), items, resources.GoldLimits, resources.EpicPieces);
            That(failureSupplies.Accepted && failureSupplies.Plan is not null, "概率失败准备真实九级卡和全部材料");
            var failureSeed = repository.CommitInventory(owner, beadSaved, failureSupplies.Plan!);
            var failureSource = failureSeed with { Level = 99, ExpertJobExperience = levelNineExperience,
                ExpertJobRecipes = beadSaved.ExpertJobRecipes.Add(highRecipe.RecipeId) };
            var failureCard = failureSource.Inventory.Items.Single(item => item.Core.ItemId == highCard);
            That(scripts.CardSuccessPercent(9, highRecipe.Rarity) < 100 &&
                ExpertJobCraft.TryPlan(failureSource, scripts, items, resources.GoldLimits, resources.EpicPieces,
                    (uint)highRecipe.RecipeId, 1, (ushort)failureCard.Position.Index, _ => 99, DateTimeOffset.UtcNow, out var failedBead, out _) &&
                failedBead.SuccessCount == 0 && failedBead.FailureCount == 1 && failedBead.Products.Length == 0 &&
                failedBead.Experience == failureSource.ExpertJobExperience && failedBead.Inventory.After.Find(failureCard.Position) is null,
                "真实概率失败消耗卡与成本，但不发宝珠、不增加成功经验且失败计数准确");
            var conversionMaterials = InventoryRewardGrantService.TryGrantBatch(beadSaved, [new(3035, 20), new(3302, 2)],
                items, resources.GoldLimits, resources.EpicPieces);
            That(conversionMaterials.Accepted && conversionMaterials.Plan is not null, "炼金转换准备钱包和实体材料");
            var alchemist = beadSaved with { ExpertJobType = 2, ExpertJobExperience = int.MaxValue, Inventory = conversionMaterials.Plan!.After };
            That(ExpertJobCraft.TryPlan(alchemist, scripts, items, resources.GoldLimits, resources.EpicPieces, 2600401, 2,
                ushort.MaxValue, _ => 0, DateTimeOffset.UtcNow, out var converted, out _) &&
                converted.Products.SequenceEqual(new[] { new ExpertJobProduct(3033, 20) }) &&
                CurrencyWallet.Get(converted.Inventory.After, CurrencyKind.BlackCube) == 20 &&
                CurrencyWallet.Get(converted.Inventory.After, CurrencyKind.RedCube) == 0,
                "共享普通配方批量转换两次产出20黑晶体并扣20红晶体，不丢输出数量");
            passed = true;
            void Sql(string sql) { using var db = database.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        }
        finally { SqliteConnection.ClearAllPools(); if (passed) Directory.Delete(root, true); else Console.WriteLine("FAILED fixture retained: " + root); }
        Console.WriteLine($"PASS {Count - checks} 副职业制作资源、钱包成本与原子提交断言");
    }
}
