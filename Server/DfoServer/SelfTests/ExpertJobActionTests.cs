using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
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
        foreach (byte job in new byte[] { 1, 2, 3, 4 })
        {
            int cap = scripts.ClampExperience(job, int.MaxValue);
            That(cap > scripts.ExperienceForLevel(job, scripts.MaxLevel(job)) &&
                scripts.AddExperience(job, cap - 1, 1) == cap && scripts.AddExperience(job, cap - 1, 2) == cap &&
                scripts.AddExperience(job, int.MaxValue, long.MaxValue) == cap && scripts.AddExperience(job, 0, 0) == 0,
                "四副职业按实际末档经验封顶，保留满级经验条并避免整数溢出：" + job);
        }
        That(scripts.DisjointRules.TryGet(0, 1, 0, out var ordinary) && ordinary.AdditionalTable == 1 &&
            ordinary.BigWinTable == 1 && ordinary.BigWinPercent == 9 && scripts.DisjointRules.Pool(1, false).Count == 4 &&
            scripts.DisjointRules.OwnMachineBigWinPercent(9) == 5,
            "当前PVF分解师完整八列、四色池和9级自用5个百分点加成已装配");
        var items = resources.Items;
        var purple = items.Get(10400) with { Rarity = 2, MinimumLevel = 95, Grade = 95 };
        var npcRewards = DisjointResultCalculator.Calculate(resources.Disjoint, purple, new Random(37));
        var machineGold = ExpertJobCraft.DisjointProducts(1, scripts.DisjointRules, purple,
            InventoryRewardGrantService.Create(purple), _ => 0);
        var machineNormal = ExpertJobCraft.DisjointProducts(1, scripts.DisjointRules, purple,
            InventoryRewardGrantService.Create(purple), _ => 9999);
        That(npcRewards.All(row => row.ItemId != 3262) && machineGold.Contains(new InventoryReward(3262, 30)) &&
            machineNormal.All(row => row.ItemId != 3262) && machineNormal.Any(row => row.ItemId == 3166),
            "普通NPC表没有金色；玩家机器紫装大成功按实际EXJ出金色，普通抽样不保底");
        That(items.Get(20238) is { Rarity: 3, BossDrop: true } && items.Get(10114).BossDrop == false,
            "当前PVF牛头兵项链的boss drop分类与普通装备区分");
        var policy = new ExpertJobCompatibility();
        var repairSource = new DfoServer.Game.Characters.Character(1, 0, "Repair", 0, towns.Room, towns.InitialReturn)
        { Level = 50, ExpertJobType = 1, ExpertJobExperience = scripts.ExperienceForLevel(1, 9),
            ExpertMachineGrade = 1, ExpertMachineEndurance = 299, Inventory = new([], Gold: 1000000) };
        That(ExpertJobCraft.TryPlanRepair(repairSource, scripts, out var repaired, out int capacity, out _) &&
            capacity == 500 && repaired.After.Gold == 979900 && repaired.CharacterPrecondition is not null,
            "Lv9附魔维修按EXJ职业等级补到500并收20100，不能误用机器档1");
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
            That(disjoint.Plan(current with { ExpertJobExperience = 1333 }, gearSlot) is { Experience: 1195 },
                "分解师满级旧经验1333按实际EXJ上限1195收敛，分解仍正常产出");
            That(disjoint.Plan(current with { ExpertJobExperience = int.MaxValue }, gearSlot) is
                { Experience: 1195, Disjoint.Rewards.Length: > 0 }, "分解师极大旧经验不阻断真实分解和产物");
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
                .First(recipe => recipe is { RequiredLevel: 9 } && items.Get(recipe.CardId).MinimumLevel <= 50 && resources.Enchantments.Get(recipe.BeadId) is { } definition &&
                    definition.Targets.Contains((targetTemplate.Kind, targetTemplate.EquipmentSlot)) &&
                    resources.Enchantments.CardMaximumLevel(recipe.CardId) > 0)!;
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
            That(ExpertJobCraft.TryPlanRepair(enchanter, scripts, out var repairWallet, out int repairEndurance, out _),
                "真实Lv9附魔角色可规划维修");
            Sql($"UPDATE character_expert_job SET expertness=0 WHERE character_id={enchanter.Id}");
            Reject<InvalidOperationException>(() => repository.CommitExpertJobMachine(owner, enchanter, repairWallet,
                enchanter.ExpertMachineGrade, repairEndurance), "资产版本相同但副职业等级已变时拒绝旧维修");
            var repairRollback = repository.LoadCharacter(owner, 1);
            That(repairRollback.Revision == enchanter.Revision && repairRollback.Inventory.Gold == enchanter.Inventory.Gold &&
                repairRollback.ExpertJobExperience == 0 && repairRollback.ExpertMachineEndurance == enchanter.ExpertMachineEndurance,
                "旧资格维修失败回滚费用、版本和耐久，保留实际新资格");
            Sql($"UPDATE character_expert_job SET expertness={enchanter.ExpertJobExperience} WHERE character_id={enchanter.Id}");
            var enchanting = new ExpertJobEnchanting(scripts, items, resources.Enchantments,
                policy with { EnchantSuccessPercent = 100 }, random: _ => 0);
            That(enchanting.Plan(enchanter with { ExpertJobRecipes = ImmutableHashSet<int>.Empty }, cardRecipe.RecipeId, gearSlot, cardSlot) is not null &&
                enchanting.Plan(enchanter, cardRecipe.RecipeId + 1, gearSlot, cardSlot) is null,
                "达到自动等级无需消费资格卷轴，伪造配方仍拒绝");
            That(enchanting.Plan(enchanter with { ExpertJobExperience = 0 }, cardRecipe.RecipeId, gearSlot, cardSlot) is null,
                "Lv1不能附魔要求Lv9的卡片，不为截图错误提示取消真实等级门禁");
            var enchanted = enchanting.Plan(enchanter, cardRecipe.RecipeId, gearSlot, cardSlot)!;
            That(enchanting.Plan(enchanter with { ExpertJobExperience = int.MaxValue }, cardRecipe.RecipeId, gearSlot, cardSlot)
                is { Succeeded: true, Experience: 1195 }, "满级溢出角色仍正常附魔，卡片成功不再突破经验上限");
            That(enchanted is { Succeeded: true } && enchanted.Inventory.After.Find(cardSlot) is null &&
                enchanted.Inventory.After.Find(gearSlot)!.Core.EnchantCardId == cardRecipe.CardId,
                "已学卡片配方真实消耗卡、材料并保留目标UID写入附魔");
            var sourceCard = enchanter.Inventory.Find(cardSlot)!;
            if (resources.Enchantments.CardMaximumLevel(cardRecipe.CardId) == 0)
                throw new InvalidDataException("附魔回归样本需要至少一个升级等级");
            var upgradedSource = enchanter with { Inventory = enchanter.Inventory with
            {
                Items = enchanter.Inventory.Items.Replace(sourceCard, sourceCard with
                { Core = sourceCard.Core with { EnchantUpgrade = 1 } })
            } };
            var upgradedPlan = enchanting.Plan(upgradedSource, cardRecipe.RecipeId, gearSlot, cardSlot);
            That(upgradedPlan is { Succeeded: true } && upgradedPlan.Inventory.After.Find(gearSlot)!.Core.EnchantUpgrade == 1 &&
                upgradedPlan.Inventory.After.Find(cardSlot) is null,
                "升级卡普通附魔真实写入源卡等级，不因配方宝珠默认等级降级");
            var enchantedTarget = upgradedSource.Inventory.Find(gearSlot)!;
            var repeated = upgradedSource with { Inventory = upgradedSource.Inventory with
            {
                Items = upgradedSource.Inventory.Items.Replace(enchantedTarget, enchantedTarget with
                { Core = enchantedTarget.Core with { EnchantCardId = cardRecipe.CardId, EnchantUpgrade = 1 } })
            } };
            That(enchanting.Plan(repeated, cardRecipe.RecipeId, gearSlot, cardSlot) is null,
                "相同卡片同一升级等级附魔拒绝重复扣料");
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
        finally { SqliteConnection.ClearAllPools(); TestFiles.DeleteDirectory(root, true); }
        InitializeExperience(towns, resources);
        Console.WriteLine($"PASS {Count - checks} 副职业兼容规则、分解/附魔原子性与回滚断言");
    }

    static void InitializeExperience(TownCatalog towns, ItemResources resources)
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-expert-exp-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var database = new SqliteGameDatabase(path);
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            foreach (byte job in new byte[] { 1, 2, 3, 4 })
            {
                using var owner = owners.Acquire("experience-cap-" + job, () => { });
                var created = repository.Create(owner, new("Experience" + job, towns.Room, towns.InitialReturn));
                Sql($"INSERT INTO character_expert_job(character_id,account,job_type,expertness) VALUES({created.Id},'experience-cap-{job}',{job},2147483647)");
                var session = new GameSession(repository, towns, owner);
                session.Select(created.Slot);
                var before = session.Selected!;
                Sql($"CREATE TRIGGER fail_exp_cap BEFORE UPDATE OF revision ON characters WHEN NEW.id={created.Id} BEGIN SELECT RAISE(ABORT,'experience cap failure'); END");
                Reject<SqliteException>(() => session.InitializeExpertJobExperience(resources.ExpertJobs),
                    "入城经验纠正末段失败整笔回滚：" + job);
                var failed = repository.LoadCharacter(owner, created.Slot);
                That(ReferenceEquals(session.Selected, before) && failed.ExpertJobExperience == int.MaxValue && failed.Revision == before.Revision,
                    "纠正失败保持会话快照、原熟练度和版本：" + job);
                Sql("DROP TRIGGER fail_exp_cap");
                int cap = resources.ExpertJobs.ClampExperience(job, int.MaxValue);
                That(session.InitializeExpertJobExperience(resources.ExpertJobs) && session.Selected!.ExpertJobExperience == cap &&
                    session.Selected.Revision == before.Revision + 1 && session.Selected.Inventory == before.Inventory,
                    "选角投影前通过原owner事务收敛，资产不变：" + job);
                var normalized = session.Selected!;
                That(!session.InitializeExpertJobExperience(resources.ExpertJobs) && ReferenceEquals(session.Selected, normalized),
                    "重复初始化不追加版本或重复写库：" + job);
                session.MarkReady();
                That(!session.InitializeExpertJobExperience(resources.ExpertJobs), "入城后不能复用选角初始化入口：" + job);
                session.ReturnToRoster(); session.Select(created.Slot);
                That(session.Selected!.ExpertJobExperience == cap && !session.InitializeExpertJobExperience(resources.ExpertJobs),
                    "重选恢复已收敛经验且无需再次纠正：" + job);
            }
            void Sql(string sql)
            {
                using var db = database.Open(); using var command = db.CreateCommand();
                command.CommandText = sql; command.ExecuteNonQuery();
            }
        }
        finally { SqliteConnection.ClearAllPools(); TestFiles.DeleteDirectory(root, true); }
    }
}
