using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonIncountTicketTests
{
    static void RunExpandedTickets(TcpTestResources resources, string root)
    {
        string path = Path.Combine(root, "expanded-incount-ticket.db");
        var repository = new CharacterRepository(path);
        var owners = new AccountOwners();
        var authentication = new AuthenticationService(new AuthenticationRepository(path));
        var counts = DungeonIncountCatalog.Load(resources.Archive);
        var clock = new ConsumableUseSessionTests.FakeClock();
        clock.Advance(DateTimeOffset.Parse("2026-11-04T05:59:40+08:00") - clock.GetUtcNow());
        var limits = ClearRewardLimitCatalog.Load(resources.Archive);
        var service = new DungeonIncountTicketService(resources.ItemResources.Items, counts, resources.Towns, clock, limits);
        // 账号旧目标已从本版dungeon.lst移除；使用保留次数表单独验证存储合同，不伪装正式副本验收。
        var quotaCounts = DungeonIncountCatalog.Parse(resources.Archive.ReadTokens(DungeonIncountCatalog.Path), resources.Archive.ResolveString);
        var quotaService = new DungeonIncountTicketService(resources.ItemResources.Items, quotaCounts, resources.Towns, clock, limits);
        var slot = new InventorySlot(InventoryListType.Main, 65);
        int[] accountIds = [490002303, 490003513, 490003524, 10159439, 10157319, 490006380, 490002847,
            490005340, 490010216, 10304090, 490006816, 10304301, 10304441, 490007904];
        foreach (int id in accountIds)
        {
            string account = "expanded-ticket-" + id;
            authentication.Create(account, "fixture-password");
            using var owner = owners.Acquire(account, () => { });
            var candidate = CharacterCreation.Prepare("Et" + id, resources.Characters.Get(0), resources.Towns.Room, resources.Towns.InitialReturn, null);
            repository.Create(owner, candidate with { Level = 95, Inventory = candidate.Inventory with
            { Items = candidate.Inventory.Items.Add(new(0, slot, new(id, ItemKind.Consumable, 1))) } });
            var expected = repository.LoadCharacter(owner, 0);
            Reject<InvalidDataException>(() => service.Prepare(expected, slot, 155), "已移除账号副本目标不可消费券：" + id);
            That(QuestCompletionRepositoryTests.Same(expected, repository.LoadCharacter(owner, 0)), "目标缺失拒绝不扣券或资格：" + id);
            if (resources.ItemResources.Items.ActionConsumable(id)!.Parameters.Contains(4137))
            {
                // 4137连账号次数行也已移除，不能为覆盖率伪造本版基础额度。
                Reject<InvalidDataException>(() => quotaService.Prepare(expected, slot, 155), "欲望之塔缺少次数行时同样拒绝");
                continue;
            }
            var plan = quotaService.Prepare(expected, slot, 155);
            var targets = plan.AccountQuotas;
            That(!targets.IsEmpty && targets.All(row => row.Amount == (id is 10159439 or 490010216 ? 5 : 1)),
                "两种账号追加格式完整保留副本名册与实际次数：" + id);
            Sql($"CREATE TRIGGER extra_ticket_abort BEFORE INSERT ON account_dungeon_incount_extra WHEN NEW.dungeon={targets[^1].Dungeon} BEGIN SELECT RAISE(ABORT,'last target rollback'); END");
            Reject<SqliteException>(() => repository.CommitDungeonIncountTicket(owner, plan, clock.GetUtcNow()), "账号券最后目标失败：" + id);
            That(QuestCompletionRepositoryTests.Same(expected, repository.LoadCharacter(owner, 0)) &&
                targets.All(target => repository.LoadAccountDungeonIncountExtra(owner, target.Dungeon, target.Period) == 0),
                "多目标故障回滚源、冷却、日限次、revision和此前目标：" + id);
            Sql("DROP TRIGGER extra_ticket_abort");
            var used = repository.CommitDungeonIncountTicket(owner, plan, clock.GetUtcNow());
            That(used.Character.Inventory.Find(slot) is null && targets.All(target =>
                repository.LoadAccountDungeonIncountExtra(owner, target.Dungeon, target.Period) == target.Amount &&
                repository.LoadAccountDungeonIncount(owner, target.Dungeon, target.Period) == 0), "账号追加不伪造负数或重置已用次数：" + id);
            Reject<InvalidDataException>(() => service.Prepare(used.Character, slot, 155), "空槽重放不追加：" + id);
            // 经真实入场事务消费基础与购买额度，最后一次之后不能继续。
            var first = targets[0];
            var row = counts.FindAccount(first.Dungeon)!;
            var charge = ImmutableArray.Create(new AccountIncountCharge(first.Dungeon, first.Period, row.Limit, true));
            for (int i = 0; i < row.Limit + first.Amount; i++)
                That(repository.CommitDungeonIncountEntry(owner, used.Character, first.Dungeon, plan.Period, null, null, charge) is not null,
                    "账号追加额度接入原仓储准入合同：" + id);
            That(repository.CommitDungeonIncountEntry(owner, used.Character, first.Dungeon, plan.Period, null, null, charge) is null,
                "账号额度恰好耗尽后拒绝：" + id);
            repository = new CharacterRepository(path);
            That(repository.LoadAccountDungeonIncountExtra(owner, first.Dungeon, first.Period) == first.Amount &&
                repository.LoadCharacter(owner, 0).Inventory.Find(slot) is null, "账号追加与扣券重开库恢复：" + id);
            int nextPeriod = DungeonIncountPolicy.AccountPeriod(counts, row, clock.GetUtcNow().AddDays(1));
            That(nextPeriod != first.Period && repository.LoadAccountDungeonIncountExtra(owner, first.Dungeon, nextPeriod) == 0,
                "账号日额度06点失效：" + id);
        }
        foreach (int id in new[] { 10093452, 10147358, 10306644 })
        {
            using var owner = owners.Acquire("expanded-character-ticket-" + id, () => { });
            var candidate = CharacterCreation.Prepare("Ec" + id, resources.Characters.Get(0), resources.Towns.Room, resources.Towns.InitialReturn, null);
            repository.Create(owner, candidate with { Level = 95, Inventory = candidate.Inventory with
            { Items = candidate.Inventory.Items.Add(new(0, slot, new(id, ItemKind.Consumable, 1))) } });
            var expected = repository.LoadCharacter(owner, 0);
            var plan = service.Prepare(expected, slot, id == 10306644 ? 214u : 104u);
            That(plan.Row?.CountKey == (id == 10306644 ? 291100320 : 4000), "角色券绑定真实次数行：" + id);
            if (id == 10306644)
            {
                That(plan.RewardQuotas.Length == 3 && plan.RewardQuotas.Count(quota => quota.Kind == 1) == 2,
                    "泰波尔斯完整保存组与两种容器额度");
                Sql("CREATE TRIGGER reward_ticket_abort BEFORE INSERT ON character_dungeon_reward_extra WHEN NEW.target=10306736 BEGIN SELECT RAISE(ABORT,'reward rollback'); END");
                Reject<SqliteException>(() => repository.CommitDungeonIncountTicket(owner, plan, clock.GetUtcNow()), "泰波尔斯最后奖励额度失败");
                That(QuestCompletionRepositoryTests.Same(expected, repository.LoadCharacter(owner, 0)) &&
                    repository.LoadDungeonIncount(owner, expected.Id, plan.Row!.CountKey, plan.Period) == default &&
                    repository.LoadClearRewardLimitUsage(owner, expected.Id, limits.FindDungeon(291100320)!, plan.Period) == default,
                    "泰波尔斯奖励额度故障回滚入场额度与源券");
                Sql("DROP TRIGGER reward_ticket_abort");
            }
            var used = repository.CommitDungeonIncountTicket(owner, plan, clock.GetUtcNow());
            That(used.Usage.DailyExtra == 1 && used.Usage.WeeklyExtra == (id == 10306644 ? 1 : 0), "角色追加按实际日周上限增加：" + id);
            if (id == 10306644)
            {
                repository = new CharacterRepository(path);
                var reward = limits.FindDungeon(291100320)!;
                var usage = repository.LoadClearRewardLimitUsage(owner, expected.Id, reward, plan.Period);
                That(usage.DailyExtra == 1 && usage.WeeklyExtra == 1 &&
                    ClearRewardLimitPolicy.CanGrantDungeonReward(reward, usage with { DailyUsed = 3, WeeklyUsed = 6 }),
                    "泰波尔斯券实际恢复3/6后的材料奖励资格且重开库存档不丢失");
                var prices = SelfTestCatalogs.CardPrices(resources.Archive);
                var policy = new TayberrsCardRewardPolicy(prices, resources.ItemResources.Boosters, resources.ItemResources.Items, limits);
                foreach (var (dungeon, container) in new[] { (291100320, 10305620), (291100322, 10306736) })
                {
                    var itemUsage = repository.LoadClearRewardLimitUsage(owner, expected.Id, reward, plan.Period, container);
                    var definition = resources.Dungeons.Get(dungeon);
                    That(policy.LimitedContainer(dungeon) == container && itemUsage is { DailyExtra: 1, WeeklyExtra: 1 } &&
                        policy.Prepare(definition, 1, _ => 0, clock.GetUtcNow(), usage with { DailyUsed = 3, WeeklyUsed = 6 },
                            itemUsage with { DailyUsed = 3, WeeklyUsed = 6 }).TayberrsWish is not null,
                        "两种实际奖励容器都可消费追加资格：" + container);
                    That(policy.Prepare(definition, 1, _ => 0, clock.GetUtcNow(), usage with { DailyUsed = 3, WeeklyUsed = 6 },
                        itemUsage with { DailyUsed = 4, WeeklyUsed = 7 }).Item is null,
                        "容器额度耗尽独立阻止发奖，不只读取组额度：" + container);
                }
                var nextDay = DungeonIncountPolicy.Period(clock.GetUtcNow().AddDays(1));
                var nextWeek = DungeonIncountPolicy.Period(clock.GetUtcNow().AddDays(2));
                That(repository.LoadClearRewardLimitUsage(owner, expected.Id, reward, nextDay) is { DailyExtra: 0, WeeklyExtra: 1 } &&
                    repository.LoadClearRewardLimitUsage(owner, expected.Id, reward, nextWeek) is { DailyExtra: 0, WeeklyExtra: 0 },
                    "泰波尔斯奖励日周额度分别失效");
                That(new[] { 10305620, 10306736 }.All(container =>
                    repository.LoadClearRewardLimitUsage(owner, expected.Id, reward, nextDay, container) is { DailyExtra: 0, WeeklyExtra: 1 } &&
                    repository.LoadClearRewardLimitUsage(owner, expected.Id, reward, nextWeek, container) is { DailyExtra: 0, WeeklyExtra: 0 }),
                    "两种容器额度存档同样按日周分别失效");
            }
        }
        foreach (int id in new[] { 10092903, 10158121 })
        {
            using var owner = owners.Acquire("expanded-reset-ticket-" + id, () => { });
            var candidate = CharacterCreation.Prepare("Er" + id, resources.Characters.Get(0), resources.Towns.Room, resources.Towns.InitialReturn, null);
            repository.Create(owner, candidate with { Level = 95, Inventory = candidate.Inventory with
            { Items = candidate.Inventory.Items.Add(new(0, slot, new(id, ItemKind.Consumable, 1))) } });
            var expected = repository.LoadCharacter(owner, 0);
            var plan = service.Prepare(expected, slot, id == 10092903 ? 59u : 160u);
            Reject<InvalidOperationException>(() => repository.CommitDungeonIncountTicket(owner, plan, clock.GetUtcNow()),
                "未有对应当日使用或奖励进度时不浪费重置券：" + id);
            That(QuestCompletionRepositoryTests.Same(expected, repository.LoadCharacter(owner, 0)), "无变化拒绝保留完整角色：" + id);
            if (id == 10092903)
            {
                var row = counts.Find(4000)!;
                repository.CommitDungeonIncountEntry(owner, expected, row.CountKey, plan.Period, row, null);
                var used = repository.CommitDungeonIncountTicket(owner, plan, clock.GetUtcNow());
                That(used.Character.Inventory.Find(slot) is null && repository.LoadDungeonIncount(owner, expected.Id, 4000, plan.Period)
                    is { DailyUsed: 0, WeeklyUsed: 1 }, "初始化仅清当天已用次数，周事实保持");
            }
            else
            {
                Sql($"INSERT INTO character_anton_normal_progress(character_id,account,day,branch,progress) VALUES({expected.Id},'{owner.Account}',{plan.Period.Day},2,6)");
                Sql("CREATE TRIGGER anton_ticket_abort BEFORE UPDATE ON character_anton_normal_progress BEGIN SELECT RAISE(ABORT,'anton rollback'); END");
                Reject<SqliteException>(() => repository.CommitDungeonIncountTicket(owner, plan, clock.GetUtcNow()), "普通安徒恩重置末步故障");
                That(QuestCompletionRepositoryTests.Same(expected, repository.LoadCharacter(owner, 0)) &&
                    repository.LoadAntonNormalProgress(owner, expected.Id).Completed == 6, "重置失败不扣委托、不消耗每日资格");
                Sql("DROP TRIGGER anton_ticket_abort");
                var used = repository.CommitDungeonIncountTicket(owner, plan, clock.GetUtcNow());
                repository = new CharacterRepository(path);
                That(repository.LoadAntonNormalProgress(owner, expected.Id) == new AntonNormalProgress(plan.Period.Day, 2, 0) &&
                    used.Character.Inventory.Find(slot) is null, "普通安徒恩保留当天路线，恢复讨伐奖励进度且重开库保持");
            }
        }

        using (var owner = owners.Acquire("reset-session-eligibility", () => { }))
        {
            var candidate = CharacterCreation.Prepare("ResetReady", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, null);
            repository.Create(owner, candidate with { Level = 95, Inventory = candidate.Inventory with
            { Items = candidate.Inventory.Items.Add(new(0, slot, new(10092903, ItemKind.Consumable, 1))) } });
            var session = new GameSession(repository, resources.Towns, owner, incounts: counts, timeProvider: clock);
            try
            {
                session.Select(0); session.MarkReady();
                var expected = session.Selected!;
                That(session.UseDungeonIncountTicket(slot, service, repository, requestedAction: 59).Outcome == ConsumableUseOutcome.Rejected &&
                    QuestCompletionRepositoryTests.Same(expected, repository.LoadCharacter(owner, expected.Slot)),
                    "无次数可重置是普通拒绝，保留券、冷却、日资格和连接");
                var row = counts.Find(4000)!;
                repository.CommitDungeonIncountEntry(owner, expected, row.CountKey, DungeonIncountPolicy.Period(clock.GetUtcNow()), row, null);
                That(session.UseDungeonIncountTicket(slot, service, repository, requestedAction: 59).Outcome == ConsumableUseOutcome.Committed,
                    "业务拒绝不残留提交不明门闩，取得真实资格后同连接可再用");
                That(session.UseDungeonIncountTicket(slot, service, repository, requestedAction: 59).Outcome == ConsumableUseOutcome.Rejected,
                    "实际扣券后同连接重放不再次重置");
            }
            finally { session.End(); }
        }

        void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
    }
}
