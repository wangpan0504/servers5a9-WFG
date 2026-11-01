using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ExpertJobLearningTests
{
    public static void Run(TownCatalog towns, ItemResources items, ExpertJobScriptCatalog scripts)
    {
        ExpertJobCraftTests.Run(towns, items);
        ExpertJobGiveUpTests.Run(towns, items);
        ExpertJobOtherLearningTests.Run(towns, items);
        int checks = Count;
        foreach (var (id, level, rarity) in new[] { (10015129, 1, 0), (10015130, 4, 1), (10015131, 7, 2),
            (10015132, 9, 3), (10159564, 10, 6), (10015151, 11, 4) })
            That(scripts.TryLearningRecipe(id, out var recipe) && recipe.RequiredLevel == level && recipe.Rarity == rarity,
                "真实附魔卷轴等级与稀有度完整映射：" + id);
        That(scripts.TryLearningRecipe(2600141, out var craft) && craft.RequiredLevel == 1 && craft.Rarity is null,
            "制作图学习不将商店need material当成制作/学习成本");
        That(!scripts.TryLearningRecipe(490705314, out _) && scripts.TryLearningRecipe(2600149, out var alchemy) && alchemy.Job == 2,
            "宝珠不是图纸，炼金图纸归属真实副职业");
        string root = Path.Combine(Path.GetTempPath(), "s5a9-expert-learning-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var database = new SqliteGameDatabase(path);
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("expert-learning", () => { });
            var template = items.Items.Get(2600141);
            short first = ItemSlotBoundService.Range(template.Kind, InventorySnapshot.Empty).Start;
            InventorySlot Slot(int offset) => new(InventoryListType.Main, (short)(first + offset));
            var scroll = InventoryRewardGrantService.Create(template);
            var blueprint = InventoryRewardGrantService.Create(items.Items.Get(2600142));
            var rare = InventoryRewardGrantService.Create(items.Items.Get(10015130));
            var created = repository.Create(owner, new("LearnScroll", towns.Room, towns.InitialReturn)
            {
                Level = 50, Inventory = new([new(0, Slot(0), scroll), new(0, Slot(1), scroll),
                    new(0, Slot(2), blueprint), new(0, Slot(3), rare)], Gold: 999)
            });
            Sql("INSERT INTO character_expert_job(character_id,account,job_type,expertness) VALUES(" + created.Id + ",'expert-learning',1,0)");
            var current = repository.LoadCharacter(owner, 0);
            // 每个自动门槛及其前一级验证资源合同；网络用例只保留代表性Lv11与真实601。
            var automatic = new (int Level, int Recipe, byte Rarity)[]
            { (1, 10015129, 0), (4, 10015130, 1), (7, 10015131, 2), (9, 10015132, 3), (10, 10159564, 6), (11, 10015151, 4) };
            foreach (int level in automatic.SelectMany(row => new[] { Math.Max(1, row.Level - 1), row.Level }).Distinct())
            {
                var grown = current with { ExpertJobExperience = scripts.ExperienceForLevel(1, level) };
                int[] expected = automatic.Where(row => row.Level <= level).Select(row => row.Recipe).Order().ToArray();
                byte[] expectedRarities = automatic.Where(row => row.Level <= level).Select(row => row.Rarity).Order().ToArray();
                That(scripts.LearnedRecipes(1, level, grown.ExpertJobRecipes).SequenceEqual(expected),
                    "等级门槛自动派生完整配方：" + level);
                var mixed = scripts.LearnedRecipes(1, level, new[] { 2600141, 10015129 });
                That(mixed.SequenceEqual(expected.Append(2600141).Order()), "有效集合保留非自动制作图并去重基础资格");
                var full = DfoServer.Network.Builders.ExpertJobPackets.Info(grown, scripts, includeQualifications: true);
                int at = 3 + expected.Length * 4;
                That(full[2] == expected.Length && full[at] == expectedRarities.Length &&
                    full.Length == 12 + expected.Length * 4 + expectedRarities.Length,
                    "205全量配方与资格数量及包长对应等级");
                Bytes(full.AsSpan(3, expected.Length * 4), expected.SelectMany(BitConverter.GetBytes).ToArray(), "205全量配方身份");
                Bytes(full.AsSpan(at + 1, expectedRarities.Length), expectedRarities, "205全量资格身份");
                var repeated = DfoServer.Network.Builders.ExpertJobPackets.Info(grown, scripts, previous: grown);
                That(repeated[at] == 0, "同等级重复通知不重复追加资格");
                if (level > 1)
                {
                    var previous = grown with { ExpertJobExperience = scripts.ExperienceForLevel(1, level - 1) };
                    var delta = DfoServer.Network.Builders.ExpertJobPackets.Info(grown, scripts, previous: previous);
                    byte[] added = automatic.Where(row => row.Level == level).Select(row => row.Rarity).ToArray();
                    That(delta[at] == added.Length, "跨门槛通知仅追加本次资格");
                    Bytes(delta.AsSpan(at + 1, added.Length), added, "205新增资格身份");
                }
            }
            var service = new ExpertJobLearning(scripts, items.Items);
            var cardCore = InventoryRewardGrantService.Create(items.Items.Get(3619));
            var cardSlot = new InventorySlot(InventoryListType.Main, ItemSlotBoundService.Range(cardCore.Kind, InventorySnapshot.Empty).Start);
            var gearSlot = new InventorySlot(InventoryListType.Main, 3);
            var cardCharacter = current with { Inventory = new([new(900001, cardSlot, cardCore),
                new(900002, gearSlot, InventoryRewardGrantService.Create(items.Items.Get(10400)))]) };
            That(scripts.IsKnownCard(1, 3619) && !ExpertJobCraft.TryPlanEnchant(cardCharacter,
                new InventoryEnchantmentService(items.Items, items.Enchantments), scripts, 0, 3, 0, (ushort)cardSlot.Index,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), out var unsafePlan, out var unsafeExperience, out _) &&
                unsafePlan is null && unsafeExperience == current.ExpertJobExperience,
                "原直接卡片捷径不得免材料、免资格把任意卡写入装备，已核实宝珠路径另有回归");
            That(service.Plan(current with { ExpertJobType = 2 }, Slot(0), 2600141) is null &&
                service.Plan(current, Slot(3), 10015130) is null && service.Plan(current, Slot(0), 10015130) is null,
                "错误副职业、未到卷轴等级与模板不符拒绝");
            That(service.Plan(current with { ExpertJobRecipes = ImmutableHashSet.Create(2600141) }, Slot(0), 2600141) is null,
                "已学习设计图不得再次消费");
            foreach (var replacement in new[] { scroll with { EquipmentLockId = 1 }, scroll with { ExpireTime = 1 } })
            {
                var source = current.Inventory.Find(Slot(0))!;
                var changed = current with { Inventory = current.Inventory with { Items = current.Inventory.Items.Replace(source, source with { Core = replacement }) } };
                That(service.Plan(changed, Slot(0), 2600141) is null, "锁定或到期设计图拒绝学习");
            }
            var plan = service.Plan(current, Slot(0), 2600141)!;
            That(plan.Inventory.After.Gold == current.Inventory.Gold && plan.Inventory.After.Items.Length == current.Inventory.Items.Length - 1,
                "学习只消费源图一张，不扣制作材料、商店费用或熟练度");
            Sql("CREATE TRIGGER fail_recipe BEFORE UPDATE OF learned_recipes ON character_expert_job BEGIN SELECT RAISE(ABORT,'recipe failure'); END");
            Reject<SqliteException>(() => repository.CommitExpertJobLearning(owner, plan), "配方写入失败整笔回滚");
            var rollback = repository.LoadCharacter(owner, 0);
            That(rollback.Revision == current.Revision && rollback.Inventory.Find(Slot(0)) == current.Inventory.Find(Slot(0)) && rollback.ExpertJobRecipes.IsEmpty,
                "扣图已执行后资格末段失败仍保留原UID、数量、版本和资格");
            Sql("DROP TRIGGER fail_recipe");
            var learned = repository.CommitExpertJobLearning(owner, plan);
            var reopened = new CharacterRepository(path).LoadCharacter(owner, 0);
            That(learned.Revision == current.Revision + 1 && learned.ExpertJobExperience == 0 && learned.Inventory.Gold == 999 &&
                learned.ExpertJobRecipes.SetEquals([2600141]) && reopened.ExpertJobRecipes.SetEquals(learned.ExpertJobRecipes) &&
                reopened.Inventory.Find(Slot(0)) is null && reopened.Inventory.Find(Slot(1)) is not null,
                "学习与扣图只增一次revision，重开库恢复真实资格，保留重复实体");
            Reject<InvalidOperationException>(() => repository.CommitExpertJobLearning(owner, plan), "旧学习计划不能重放扣图");
            var next = service.Plan(reopened, Slot(2), 2600142)!;
            Sql("UPDATE character_expert_job SET expertness=20");
            Reject<InvalidOperationException>(() => repository.CommitExpertJobLearning(owner, next), "同revision副职业资格变化也拒绝旧计划");
            That(repository.LoadCharacter(owner, 0).Inventory.Find(Slot(2)) is not null, "旧资格拒绝回滚已经执行的扣图");
            // 在规划后、写事务前到期，也不能消费源图。
            var expiryCurrent = repository.LoadCharacter(owner, 0);
            var blueprintSource = expiryCurrent.Inventory.Find(Slot(2))!;
            var expiringInventory = expiryCurrent.Inventory with { Items = expiryCurrent.Inventory.Items.Replace(blueprintSource,
                blueprintSource with { Core = blueprintSource.Core with { ExpireTime = 2000000000 } }) };
            expiryCurrent = repository.CommitInventory(owner, expiryCurrent, new(expiryCurrent.Inventory, expiringInventory, "learning_expiry_fixture"));
            var clock = new Clock { Now = 1999999999 };
            var expiryPlan = new ExpertJobLearning(scripts, items.Items, clock).Plan(expiryCurrent, Slot(2), 2600142)!;
            clock.Now++;
            Reject<InventoryQualificationExpiredException>(() => repository.CommitExpertJobLearning(owner, expiryPlan), "提交时到期拒绝原计划");
            That(repository.LoadCharacter(owner, 0).Revision == expiryCurrent.Revision &&
                repository.LoadCharacter(owner, 0).Inventory.Find(Slot(2)) == expiryCurrent.Inventory.Find(Slot(2)),
                "提交时到期保持源UID及版本");
            var cleared = repository.CommitExpertJobClear(owner, repository.LoadCharacter(owner, 0));
            That(cleared.ExpertJobRecipes.IsEmpty && repository.LoadCharacter(owner, 0).ExpertJobRecipes.IsEmpty,
                "放弃副职业清理当前与持久化已学图，不泄漏到下一职业");
            DatabaseSchemaTests.RewindRaidLifecycle(path);
            using (var db = database.Open()) DatabaseSchemaTests.RemoveEmptyPromotionFixtureSchema(db);
            Sql(DatabaseSchemaTests.GuildExtensionFixtureRewindSql + "ALTER TABLE character_expert_job DROP COLUMN learned_recipes; ALTER TABLE characters DROP COLUMN selected_skill_tree; PRAGMA user_version=77");
            _ = new SqliteGameDatabase(path);
            That(repository.LoadCharacter(owner, 0).ExpertJobRecipes.IsEmpty, "v77旧库升级为空学习集合，不凭等级补造学习记录");

            void Sql(string sql)
            {
                using var db = database.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }
        finally { SqliteConnection.ClearAllPools(); TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - checks} 副职业设计图资源、扣图原子性与重开恢复断言");
    }
    sealed class Clock : TimeProvider
    {
        public long Now { get; set; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Now);
    }
}
