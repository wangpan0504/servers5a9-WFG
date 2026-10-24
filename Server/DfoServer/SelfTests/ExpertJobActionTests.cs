using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using DfoServer.GameWorld.Towns;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ExpertJobActionTests
{
    public static void Run(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        var scripts = resources.ExpertJobs;
        That(scripts.DisjointRules.TryGet(0, 1, 0, out var ordinary) && ordinary.AdditionalTable == 1 &&
            ordinary.BigWinTable == 1 && ordinary.BigWinPercent == 9 && scripts.DisjointRules.Pool(1, false).Count == 4 &&
            scripts.DisjointRules.OwnMachineBigWinPercent(9) == 5,
            "当前PVF分解师完整八列、四色池和9级自用5个百分点加成已装配");
        var items = resources.Items;
        That(items.Get(20238) is { Rarity: 3, BossDrop: true } && items.Get(10114).BossDrop == false,
            "当前PVF牛头兵项链的boss drop分类与普通装备区分");
        var policy = new ExpertJobCompatibility();
        foreach (var invalid in new[] { policy with { DisjointEnduranceCost = 0 }, policy with { EnchantSuccessPercent = 101 },
            policy with { SelfServiceRewardPercent = -1 }, policy with { DisjointRewardPermille = 10001 } })
            Reject<InvalidDataException>(() => invalid.Validate(), "兼容配置越界启动拒绝");
        var disjoint = new ExpertJobDisassembly(new InventoryDisjointService(items, resources.Disjoint,
            resources.GoldLimits, resources.EpicPieces, expertJobs: scripts), scripts, items, resources.GoldLimits,
            resources.EpicPieces, policy, random: limit => limit == 100 ? 0 : limit - 1);
        string root = Path.Combine(Path.GetTempPath(), "s5a9-profession-action-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var database = new SqliteGameDatabase(path);
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("profession-action", () => { });
            var gearSlot = new InventorySlot(InventoryListType.Main, 3);
            var created = repository.Create(owner, new("MachineTest", towns.Room, towns.InitialReturn)
            {
                Level = 50, Inventory = new([new(0, gearSlot, InventoryRewardGrantService.Create(items.Get(10114)))], Gold: 999)
            });
            Sql($"INSERT INTO character_expert_job(character_id,account,job_type,expertness) VALUES({created.Id},'profession-action',3,51)");
            var current = repository.LoadCharacter(owner, 0);
            var white = current with { Inventory = current.Inventory with { Items =
                [current.Inventory.Find(gearSlot)! with { Core = InventoryRewardGrantService.Create(items.Get(10400)) }] } };
            That(disjoint.Plan(white, gearSlot)!.Disjoint!.Rewards.All(row => row.ItemId != 2610045),
                "白装在本人机器中也不触发仅限稀有度大于1的自用零件");
            var plan = disjoint.Plan(current, gearSlot)!;
            That(plan is not null && plan.Disjoint!.Rewards.Any(row => row.ItemId == 2610045 && row.Count == 1) &&
                plan.Inventory.After.Find(gearSlot) is null && plan.Endurance == 299 && plan.Experience > 51,
                "个人分解消费真实装备、给予基础产物与自用零件，规划耐久和经验");
            That(disjoint.Plan(current with { ExpertMachineEndurance = 0 }, gearSlot) is null &&
                disjoint.Plan(current with { ExpertJobType = 1 }, gearSlot) is null, "无耐久或非分解师不规划");
            Sql("CREATE TRIGGER fail_machine BEFORE INSERT ON character_expert_meta BEGIN SELECT RAISE(ABORT,'machine failure'); END");
            Reject<SqliteException>(() => repository.CommitExpertJobAction(owner, plan!), "资产和经验已写后耐久失败整笔回滚");
            var rollback = repository.LoadCharacter(owner, 0);
            That(rollback.Revision == current.Revision && rollback.Inventory.Find(gearSlot) == current.Inventory.Find(gearSlot) &&
                rollback.ExpertJobExperience == 51 && rollback.ExpertMachineEndurance == 300,
                "分解末段故障保持源UID、钱包、经验与耐久");
            Sql("DROP TRIGGER fail_machine");
            var committed = repository.CommitExpertJobAction(owner, plan!);
            That(committed.Revision == current.Revision + 1 && committed.ExpertMachineEndurance == 299 &&
                committed.Inventory.Find(gearSlot) is null && committed.ExpertJobExperience == plan!.Experience,
                "真实分解所有资产和副职业状态同一次revision提交");
            Reject<InvalidOperationException>(() => repository.CommitExpertJobAction(owner, plan!), "机器旧计划不能重放");

            var targetTemplate = items.Get(10400);
            var cardRecipe = scripts.KnownCards(1).Order().Select(id => scripts.TryCardRecipe(id, out var recipe) ? recipe : null)
                .First(recipe => recipe is not null && resources.Enchantments.Get(recipe.BeadId) is { } definition &&
                    definition.Targets.Contains((targetTemplate.Kind, targetTemplate.EquipmentSlot)))!;
            var cardCore = InventoryRewardGrantService.Create(items.Get(cardRecipe.CardId));
            var cardSlot = new InventorySlot(InventoryListType.Main, ItemSlotBoundService.Range(cardCore.Kind, InventorySnapshot.Empty).Start);
            var enchanter = repository.Create(owner, new("EnchantTest", towns.Room, towns.InitialReturn)
            {
                Level = 50, Inventory = new([new(0, gearSlot, InventoryRewardGrantService.Create(targetTemplate)),
                    new(0, cardSlot, cardCore)], Gold: 1000000)
            });
            var grant = InventoryRewardGrantService.TryGrantBatch(enchanter,
                cardRecipe.Materials.Where(row => row.ItemId > 0).Select(row => new InventoryReward(row.ItemId, row.Count)).ToArray(),
                items, resources.GoldLimits, resources.EpicPieces);
            That(grant.Accepted, "匹配配方的真实材料可以授予测试角色");
            enchanter = repository.CommitInventory(owner, enchanter, grant.Plan!);
            Sql($"INSERT INTO character_expert_job(character_id,account,job_type,expertness,learned_recipes) VALUES({enchanter.Id},'profession-action',1,1195,'[{cardRecipe.RecipeId}]')");
            enchanter = repository.LoadCharacter(owner, 1);
            var enchanting = new ExpertJobEnchanting(scripts, items, resources.Enchantments,
                policy with { EnchantSuccessPercent = 100 }, random: _ => 0);
            That(enchanting.Plan(enchanter with { ExpertJobRecipes = ImmutableHashSet<int>.Empty }, cardRecipe.RecipeId, gearSlot, cardSlot) is null &&
                enchanting.Plan(enchanter, cardRecipe.RecipeId + 1, gearSlot, cardSlot) is null,
                "未学或伪造配方不能附魔");
            var enchanted = enchanting.Plan(enchanter, cardRecipe.RecipeId, gearSlot, cardSlot)!;
            That(enchanted is { Succeeded: true } && enchanted.Inventory.After.Find(cardSlot) is null &&
                enchanted.Inventory.After.Find(gearSlot)!.Core.EnchantCardId == cardRecipe.CardId,
                "已学卡片配方真实消耗卡、材料并保留目标UID写入附魔");
            Sql("CREATE TRIGGER fail_enchant BEFORE INSERT ON character_expert_meta BEGIN SELECT RAISE(ABORT,'enchant failure'); END");
            Reject<SqliteException>(() => repository.CommitExpertJobAction(owner, enchanted), "附魔末段耐久写失败回滚卡、材料及目标");
            That(repository.LoadCharacter(owner, 1).Revision == enchanter.Revision &&
                repository.LoadCharacter(owner, 1).Inventory.Find(cardSlot) is not null, "附魔故障不丢卡、不改变目标版本");
            Sql("DROP TRIGGER fail_enchant");
            var failed = new ExpertJobEnchanting(scripts, items, resources.Enchantments,
                policy with { EnchantSuccessPercent = 0 }, random: _ => 0).Plan(enchanter, cardRecipe.RecipeId, gearSlot, cardSlot)!;
            That(!failed.Succeeded && failed.Inventory.After.Find(gearSlot) == enchanter.Inventory.Find(gearSlot) &&
                failed.Inventory.After.Find(cardSlot) is null && failed.Experience == enchanter.ExpertJobExperience,
                "可配置失败按明确兼容规则消费投入、保留原附魔且不加成功经验");
            var saved = repository.CommitExpertJobAction(owner, enchanted);
            var reopened = new CharacterRepository(path).LoadCharacter(owner, 1);
            That(saved.Revision == enchanter.Revision + 1 && reopened.Inventory.Find(gearSlot)!.Core.EnchantCardId == cardRecipe.CardId &&
                reopened.Inventory.Find(gearSlot)!.Uid == enchanter.Inventory.Find(gearSlot)!.Uid && reopened.ExpertMachineEndurance == 299,
                "卡片附魔资产与经验耐久只提交一次，重开库恢复原目标UID和实际效果");
            Reject<InvalidOperationException>(() => repository.CommitExpertJobAction(owner, enchanted), "附魔旧请求不可重复消耗");
            void Sql(string sql) { using var db = database.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - checks} 副职业兼容规则、分解/附魔原子性与回滚断言");
    }
}
