using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class BoosterRandomOptionTests
{
    public static void Run(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        const string body = "[stackable type]\n`[booster]` 0\n[booster info]\n[equipment]\n1 20264 100 2\n[/equipment]\n[/booster info]\n";
        That(!BoosterCatalog.Parse(body).UnsealsRandomOptions &&
            !BoosterCatalog.Parse(body + "[random option unseal]\n0").UnsealsRandomOptions &&
            BoosterCatalog.Parse(body + "[random option unseal]\n1").UnsealsRandomOptions, "解封缺失与显式零保持封印，仅1启用");
        foreach (string bad in new[] { "", "-1", "2", "1 1", "`1`", "1\n[random option unseal]\n1", "1\n[/random option unseal]" })
            That(!BoosterCatalog.Parse(body + "[random option unseal]\n" + bad).Available, "未知或重复解封声明不放行");
        That(!BoosterCatalog.Parse(body.Replace("1 20264", "[random option unseal]\n1\n1 20264")).Available,
            "嵌套解封字段不冒充根声明");
        var now = DateTimeOffset.UtcNow;
        var random = new AlternatingRandom();
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits, resources.EpicPieces,
            random, new GameDebugSettings { IgnoreCashShopAbsoluteExpiration = true }, randomOptions: resources.RandomOptions);
        Character Role(int id, int count = 1)
        {
            var template = resources.Items.Get(id);
            byte job = (byte)Enumerable.Range(0, 16).First(value => (template.Jobs & (1 << value)) != 0);
            return new(1, 0, "UnsealBox", 0, towns.Room, towns.InitialReturn)
            {
                Job = job, Level = Math.Max((byte)1, template.MinimumLevel),
                Inventory = new([new(1, new(InventoryListType.Main, 65), InventoryRewardGrantService.Create(template, count, now, shopExpiration: true))], Gold: 12345)
            };
        }
        var role = Role(10012169, 2);
        var result = service.Open(role, new(65, 2), out var receipt, now);
        That(result.Accepted && result.Plan!.After.Items.Length == 18 && receipt!.Rewards.Sum(row => row.Count) == 18,
            "真实九件套批量两盒发放完整18件");
        That(result.Plan!.After.Gold == role.Inventory.Gold && role.Inventory.Items.Single().Core.Count == 2,
            "礼盒解封不扣CMD401费用，规划不改原资产");
        foreach (var item in result.Plan.After.Items)
        {
            var original = InventoryRewardGrantService.Create(resources.Items.Get(item.Core.ItemId), utcNow: now, shopExpiration: true);
            That(item.Core.Random0.Type != 0 && item.Core with { Random0 = default, Random1 = default, Random2 = default,
                RandomChangedIndex = original.RandomChangedIndex } == original, "魔法词条之外完整核心保持创建合同");
        }
        That(random.Calls == 36, "18件各执行数量及候选两次抽取，不复制一次抽取的原型");
        foreach (var group in result.Plan.After.Items.GroupBy(item => item.Core.ItemId))
        {
            var pool = resources.RandomOptions.ResolveBoosterReward(resources.Items.Get(group.Key))!.Pools[0];
            That(pool.Count == 1, "预定端点序列使用实际首个单词条池");
            var values = pool.Groups[0].Where(row => row.Weight > 0).ToArray();
            var expected = new[] { values[0], values[^1] }.Select(row => new RandomOption(row.Type, row.Value1, row.Value2));
            That(group.Select(item => item.Core.Random0).SequenceEqual(expected), "同ID两件分别取真实池首尾候选，按序独立冻结");
        }
        var originalCore = InventoryRewardGrantService.Create(resources.Items.Get(20264), utcNow: now, upgrade: 7, refinement: 3);
        var synthetic = new RandomOptionDefinition(999, [new(1, 1, [[new(15, 1, 2, 1), new(16, 3, 4, 1)]])]);
        var first = InventoryRandomOptionService.Initialize(originalCore, synthetic, _ => 0);
        var last = InventoryRandomOptionService.Initialize(originalCore, synthetic, bound => bound - 1);
        That(first.Random0.Type == 15 && last.Random0.Type == 16 && first.Attr == 7 && last.GenuineUpgrade == 3 &&
            first with { Random0 = default, RandomChangedIndex = originalCore.RandomChangedIndex } == originalCore &&
            last with { Random0 = default, RandomChangedIndex = originalCore.RandomChangedIndex } == originalCore,
            "预定权重端点生成不同词条，强化锻造封装期限及其余字段完整保留");
        foreach (int id in Enumerable.Range(10308573, 6))
        {
            var definition = resources.Boosters.Get(id);
            var category = definition.Selection!.Categories.First(group => group.PendingRule is null && !group.Rewards.IsEmpty);
            var choice = category.Rewards[0];
            int[] ids = definition.Selection.SelectionCount == 0 ? category.Rewards.Select(row => row.ItemId).ToArray() : [choice.ItemId];
            var opened = service.Open(Role(id), new(65, 1, new(category.Category, category.Subcategory, ids)), out _, now);
            That(opened.Accepted && opened.Plan!.After.Items.All(item => item.Core.Random0.Type != 0 &&
                category.Rewards.Any(row => row.ItemId == item.Core.ItemId && item.Core.Upgrade == row.Upgrade &&
                    item.Core.GenuineUpgrade == row.Refinement)), "六种真实选择盒保留强化锻造并解封 " + id + ":" + opened.RejectReason + ":" + opened.Error + ":" + ids.Length);
        }
        int calls = random.Calls;
        That(!service.Open(Role(10000109), new(65, 1), out _, now).Accepted && random.Calls == calls,
            "含非装备的混合解封盒在随机与扣源前拒绝");
        var range = ItemSlotBoundService.Range(ItemKind.Equipment, role.Inventory.Expansion);
        var full = role with { Inventory = role.Inventory with { Items = role.Inventory.Items.AddRange(
            Enumerable.Range(range.Start, range.End - range.Start + 1).Select(slot => new InventoryItem(slot + 100,
                new(InventoryListType.Main, (short)slot), InventoryRewardGrantService.Create(resources.Items.Get(20264))))) } };
        That(!service.Open(full, new(65, 1), out _, now).Accepted && full.Inventory.Gold == 12345, "装备页满时整盒不消费");
        Persistence(role, service, now);
        Console.WriteLine($"PASS {Count - checks} booster random option assertions");
    }

    static void Persistence(Character role, InventoryBoosterService service, DateTimeOffset now)
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-booster-unseal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("unseal-box", () => { });
            var saved = repo.Create(owner, new(role.Name, role.Position, role.Position)
            { Job = role.Job, Level = role.Level, Inventory = role.Inventory with { Items = role.Inventory.Items.Select(item => item with { Uid = 0 }).ToImmutableArray() } });
            var plan = service.Open(saved, new(65, 2), out _, now).Plan!;
            string before = JsonSerializer.Serialize(repo.List(owner));
            Sql("CREATE TRIGGER fail_unseal_box BEFORE INSERT ON character_inventory_slots WHEN NEW.slot_index=10 BEGIN SELECT RAISE(ABORT,'unseal rollback'); END");
            bool failed = false;
            try { repo.CommitInventory(owner, saved, plan); } catch (SqliteException) { failed = true; }
            That(failed && JsonSerializer.Serialize(repo.List(owner)) == before, "晚期SQL失败回滚源、全部随机核心与revision");
            Sql("DROP TRIGGER fail_unseal_box");
            repo.CommitInventory(owner, saved, plan);
            var reopened = new CharacterRepository(path).List(owner).Single();
            That(reopened.Inventory.Items.Select(item => item.Core).SequenceEqual(plan.After.Items.Select(item => item.Core)) &&
                reopened.Inventory.Items.All(item => item.Uid > 0), "提交与重读保留已经冻结的每件随机词条，不重抽");
            That(!service.Open(reopened, new(65, 1), out _, now).Accepted, "源耗尽后重放不再次发奖");
            void Sql(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
        }
        finally { Directory.Delete(root, true); }
    }

    sealed class AlternatingRandom : Random
    {
        public int Calls { get; private set; }
        public override long NextInt64(long maxValue)
        {
            // 数量池与随后的候选轮流取首/末端，避免以概率判断多件独立性。
            Calls++;
            return Calls % 4 == 0 ? maxValue - 1 : 0;
        }
    }
}
