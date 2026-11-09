using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
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
    static void RunDimensionLimits(TcpTestResources resources, string root)
    {
        string path = Path.Combine(root, "dimension-ticket-limits.db");
        var repository = new CharacterRepository(path);
        var owners = new AccountOwners();
        var clock = new ConsumableUseSessionTests.FakeClock();
        clock.Advance(DateTimeOffset.Parse("2026-10-28T05:59:40+08:00") - clock.GetUtcNow());
        var counts = DungeonIncountCatalog.Load(resources.Archive);
        var dimension = counts.Dimension!;
        var service = new DimensionEntryTicketService(resources.ItemResources.Items, clock);
        var slot = new InventorySlot(InventoryListType.Main, 65);
        var spare = new InventorySlot(InventoryListType.Main, 66);
        foreach (int id in new[] { 10158125, 10158126, 490008217, 10158692, 10161066, 490003748, 490009451 })
        {
            using var owner = owners.Acquire("dimension-limit-" + id, () => { });
            int amount = resources.ItemResources.Items.ActionConsumable(id)!.Parameters[0];
            var candidate = CharacterCreation.Prepare("Dt" + id, resources.Characters.Get(0), resources.Towns.Room, resources.Towns.InitialReturn, null);
            repository.Create(owner, candidate with { Level = 95, Inventory = candidate.Inventory with
            {
                Items = candidate.Inventory.Items.Add(new(0, slot, new(id, ItemKind.Consumable, 1) { ExpireTime = 2145852000 }))
                    .Add(new(0, spare, new(id, ItemKind.Consumable, 1) { ExpireTime = 2145852000 })),
                DailyBoosterUses = amount == 1 ? [new(DimensionEntryTicketService.SharedCounterItem,
                    ShopPurchaseCount.Today(clock.GetUtcNow()), 4)] : []
            } });
            var session = NewSession();
            try
            {
                session.Select(0); session.MarkReady();
                if (id == 490008217)
                {
                    var before = session.Selected!;
                    That(!session.UseDimensionEntryTicket(slot, service).Accepted &&
                        QuestCompletionRepositoryTests.Same(before, repository.LoadCharacter(owner, 0)),
                        "活动券基础次数未用尽时整拒且不扣源");
                    var period = DungeonIncountPolicy.Period(clock.GetUtcNow());
                    Sql($"INSERT INTO character_dungeon_incount(character_id,account,dungeon,day,daily_used,week,weekly_used) SELECT id,account,{dimension.DailyRow.CountKey},{period.Day},5,{period.Week},0 FROM characters WHERE account='dimension-limit-{id}'");
                }
                var saved = session.Selected!;
                Sql("CREATE TRIGGER dimension_limit_abort AFTER INSERT ON inventory_audit_log WHEN NEW.action_name='dimension_entry_ticket' BEGIN SELECT RAISE(ABORT,'limit rollback'); END");
                Reject<SqliteException>(() => session.UseDimensionEntryTicket(slot, service), "合法券晚期SQL故障整笔回滚：" + id);
                That(QuestCompletionRepositoryTests.Same(saved, repository.LoadCharacter(owner, 0)) &&
                    QuestCompletionRepositoryTests.Same(saved, session.Selected!), "故障保留完整库存、UID、冷却、日账本和revision：" + id);
                Sql("DROP TRIGGER dimension_limit_abort");
                That(session.UseDimensionEntryTicket(slot, service).Accepted &&
                    DimensionEntryTicketService.Extra(session.Selected!.Inventory, clock.GetUtcNow()) == dimension.ExtraLimit &&
                    session.Selected.Inventory.Find(slot) is null && session.Selected.Inventory.Find(spare) is not null,
                    "七款共用额度恰好五次，按实际1/5参数扣一张：" + id);
                var capped = session.Selected!;
                That(!session.UseDimensionEntryTicket(spare, service).Accepted &&
                    QuestCompletionRepositoryTests.Same(capped, repository.LoadCharacter(owner, 0)), "满额第二张不扣源、不续额度：" + id);
                session.End();
                repository = new CharacterRepository(path);
                session = NewSession(); session.Select(0); session.MarkReady();
                That(DimensionEntryTicketService.Extra(session.Selected!.Inventory, clock.GetUtcNow()) == 5 &&
                    session.Selected.Inventory.Find(slot) is null && session.Selected.Inventory.Find(spare)?.Core.ItemId == id,
                    "重建仓储和会话恢复完整已用额度与未消费券：" + id);
                clock.Advance(TimeSpan.FromSeconds(10));
                That(!session.UseDimensionEntryTicket(spare, service).Accepted,
                    "冷却结束仍不得越过共享每日上限：" + id);
                clock.Advance(TimeSpan.FromSeconds(10));
                That(DimensionEntryTicketService.Extra(session.Selected!.Inventory, clock.GetUtcNow()) == 0,
                    "北京时间06点释放前一天的追加额度：" + id);
                if (id != 490008217)
                    That(session.UseDimensionEntryTicket(spare, service).Accepted &&
                        DimensionEntryTicketService.Extra(session.Selected!.Inventory, clock.GetUtcNow()) == amount,
                        "新业务日可正常再消费，不继承旧日满额：" + id);
                else
                {
                    That(!session.UseDimensionEntryTicket(spare, service).Accepted, "活动券新日仍须耗尽基础次数");
                    var period = DungeonIncountPolicy.Period(clock.GetUtcNow());
                    Sql($"UPDATE character_dungeon_incount SET day={period.Day},daily_used=5 WHERE account='dimension-limit-{id}'");
                    That(session.UseDimensionEntryTicket(spare, service).Accepted, "活动券新日耗尽基础次数后恢复使用");
                }
            }
            finally { session.End(); }
            clock.Advance(DateTimeOffset.Parse("2026-10-28T05:59:40+08:00") - clock.GetUtcNow());
            GameSession NewSession() => new(repository, resources.Towns, owner, incounts: counts, timeProvider: clock);
        }
        // 同款连续请求仍遵守原10秒冷却；换款不能绕过共享剩余额度或截断5次券。
        using var coolOwner = owners.Acquire("dimension-cooldown", () => { });
        var seed = CharacterCreation.Prepare("DimCool", resources.Characters.Get(0), resources.Towns.Room, resources.Towns.InitialReturn, null);
        repository.Create(coolOwner, seed with { Level = 95, Inventory = seed.Inventory with
        { Items = seed.Inventory.Items.Add(new(0, slot, new(10158125, ItemKind.Consumable, 2)))
            .Add(new(0, spare, new(10161066, ItemKind.Consumable, 1))) } });
        var current = repository.LoadCharacter(coolOwner, 0);
        var first = service.Plan(current, slot, 5, "[seria room]");
        current = InventoryService.Commit(repository, coolOwner, current, first);
        var legacy = current with { Inventory = current.Inventory with { DailyBoosterUses =
            [new(DimensionEntryTicketService.SharedCounterItem, ShopPurchaseCount.Today(clock.GetUtcNow()), 10)] } };
        That(DimensionEntryTicketService.Extra(legacy.Inventory, clock.GetUtcNow()) == 10 &&
            !service.Plan(legacy, slot, 5, "[seria room]").Accepted,
            "旧版本已授予超过五次的存量不没收，仍禁止继续追加");
        That(!service.Plan(current, slot, 5, "[seria room]").Accepted, "重复请求不能跳过原10秒冷却");
        That(!service.Plan(current, spare, 5, "[seria room]").Accepted, "剩余四次时5次券整拒，不截断或跨款绕过");
        clock.Advance(TimeSpan.FromSeconds(10));
        That(service.Plan(current, slot, 5, "[seria room]").Accepted, "冷却到期且额度充足可正常继续追加");
        clock.Advance(TimeSpan.FromSeconds(9));
        var oldPlan = service.Plan(current, slot, 5, "[seria room]");
        clock.Advance(TimeSpan.FromSeconds(1));
        Reject<InventoryQualificationExpiredException>(() => InventoryService.Commit(repository, coolOwner, current, oldPlan),
            "05:59:59规划不能跨06:00扣券加次");

        void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
    }

    static void RunResetLimits(TcpTestResources resources, string root)
    {
        string path = Path.Combine(root, "dawn-reset-limits.db");
        var repository = new CharacterRepository(path);
        var owners = new AccountOwners();
        var clock = new ConsumableUseSessionTests.FakeClock();
        clock.Advance(DateTimeOffset.Parse("2026-10-28T05:59:30+08:00") - clock.GetUtcNow());
        var counts = DungeonIncountCatalog.Load(resources.Archive);
        var row = counts.Find(530)!;
        var service = new DungeonIncountTicketService(resources.ItemResources.Items, counts, resources.Towns, clock);
        var slot = new InventorySlot(InventoryListType.Main, 65);
        foreach (int id in new[] { 10308584, 10309767, 490011258, 490013299 })
        {
            using var owner = owners.Acquire("dawn-reset-" + id, () => { });
            var seed = CharacterCreation.Prepare("Dr" + id, resources.Characters.Get(0), resources.Towns.Room, resources.Towns.InitialReturn, null);
            repository.Create(owner, seed with { Inventory = seed.Inventory with
                { Items = seed.Inventory.Items.Add(new(0, slot, new(id, ItemKind.Consumable, 3) { ExpireTime = 2145852000 })) } });
            var session = NewSession();
            try
            {
                session.Select(0); session.MarkReady();
                That(resources.ItemResources.Items.ActionConsumable(id) is { DailyLimit: 1, CoolTime: 10000 },
                    "四款重置券读取各自每日一次及冷却：" + id);
                var before = session.Selected!;
                That(session.UseDungeonIncountTicket(slot, service, repository).Outcome == ConsumableUseOutcome.Rejected &&
                    QuestCompletionRepositoryTests.Same(before, repository.LoadCharacter(owner, 0)), "尚有次数重置整拒、不消耗每日资格：" + id);
                Exhaust();
                if (id == 10308584)
                {
                    var resetPlan = service.Prepare(session.Selected!, slot);
                    using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
                    using var command = db.CreateCommand();
                    command.CommandText = "CREATE TRIGGER reset_abort BEFORE UPDATE ON character_dungeon_incount BEGIN SELECT RAISE(ABORT,'reset rollback'); END";
                    command.ExecuteNonQuery();
                    var saved = session.Selected!;
                    Reject<SqliteException>(() => repository.CommitDungeonIncountTicket(owner, resetPlan, clock.GetUtcNow()),
                        "重置额度SQL失败回滚原库存消费");
                    That(QuestCompletionRepositoryTests.Same(saved, repository.LoadCharacter(owner, 0)) &&
                        repository.LoadDungeonIncount(owner, saved.Id, row.CountKey, resetPlan.Period).DailyUsed == row.Limit,
                        "重置失败保留完整源、每日使用次数、revision与原已用入场次数");
                    command.CommandText = "DROP TRIGGER reset_abort"; command.ExecuteNonQuery();
                }
                That(session.UseDungeonIncountTicket(slot, service, repository, requestedAction: 213).Outcome == ConsumableUseOutcome.Rejected,
                    "伪造为追加动作不能使用重置券：" + id);
                That(session.UseDungeonIncountTicket(slot, service, repository).Outcome == ConsumableUseOutcome.Committed &&
                    session.Selected!.Inventory.Find(slot)!.Core.Count == 2, "耗尽后一次恢复基础次数并扣一券：" + id);
                Exhaust(); clock.Advance(TimeSpan.FromSeconds(10));
                That(session.UseDungeonIncountTicket(slot, service, repository).Outcome == ConsumableUseOutcome.Rejected,
                    "重新耗尽及冷却到期仍不能绕过每天一次：" + id);
                session.End(); repository = new CharacterRepository(path);
                session = NewSession(); session.Select(0); session.MarkReady();
                That(session.UseDungeonIncountTicket(slot, service, repository).Outcome == ConsumableUseOutcome.Rejected &&
                    session.Selected!.Inventory.Find(slot)!.Core.Count == 2, "重建仓储和会话不能恢复当日资格：" + id);
                clock.Advance(TimeSpan.FromSeconds(20));
                That(session.UseDungeonIncountTicket(slot, service, repository).Outcome == ConsumableUseOutcome.Rejected,
                    "06点已恢复基础次数，不能立刻无条件重置：" + id);
                Exhaust();
                That(session.UseDungeonIncountTicket(slot, service, repository).Outcome == ConsumableUseOutcome.Committed &&
                    session.Selected!.Inventory.Find(slot)!.Core.Count == 1, "新日耗尽后恢复每日一次重置资格：" + id);
            }
            finally { session.End(); }
            clock.Advance(DateTimeOffset.Parse("2026-10-28T05:59:30+08:00") - clock.GetUtcNow());
            GameSession NewSession() => new(repository, resources.Towns, owner, incounts: counts, timeProvider: clock);
            void Exhaust()
            {
                var period = DungeonIncountPolicy.Period(clock.GetUtcNow());
                for (int i = 0; i < row.Limit; i++)
                    That(repository.CommitDungeonIncountEntry(owner, session.Selected!, row.CountKey, period, row, null) is not null,
                        "原入场事务实际耗尽重置券目标次数");
            }
        }
        That(resources.ItemResources.Items.ActionConsumable(10306644) is null,
            "泰波尔斯奖励容器参数尚未闭合，保留整拒而非丢弃参数发假成功");
    }
}
