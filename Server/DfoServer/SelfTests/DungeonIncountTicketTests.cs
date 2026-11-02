using DfoServer.Game.Accounts;
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

internal static class DungeonIncountTicketTests
{
    internal static void Run(TcpTestResources resources, string root)
    {
        int before = Count;
        string path = Path.Combine(root, "incount-ticket.db");
        var repository = new CharacterRepository(path);
        var owners = new AccountOwners();
        using var owner = owners.Acquire("incount-ticket", () => { });
        var clock = new ConsumableUseSessionTests.FakeClock();
        clock.Advance(DateTimeOffset.Parse("2026-10-27T05:59:59+08:00") - clock.GetUtcNow());
        var incounts = DungeonIncountCatalog.Load(resources.Archive);
        var service = new DungeonIncountTicketService(resources.ItemResources.Items, incounts, resources.Towns, clock);
        var row = incounts.Find(5428)!;
        var slot = new InventorySlot(InventoryListType.Main, 65);
        var candidate = CharacterCreation.Prepare("WarTicket", resources.Characters.Get(0), resources.Towns.Room, resources.Towns.InitialReturn, null);
        repository.Create(owner, candidate with { Inventory = candidate.Inventory with { Items = candidate.Inventory.Items
            .Add(new(0, slot, new(10310974, ItemKind.Consumable, 1) { ExpireTime = 2145852000 }))
            .Add(new(0, new(InventoryListType.Main, 66), new(10310974, ItemKind.Consumable, 1) { ExpireTime = 2145852000 })) } });
        var session = new GameSession(repository, resources.Towns, owner, incounts: incounts, timeProvider: clock);
        try
        {
            Select();
            That(resources.ItemResources.Items.ActionConsumable(10310974) is
                { Label: "[add dungeon incount item limit count]", Parameters: [5428, 0, 0] }, "正式追加券完整动作按资源识别");
            var plan = service.Prepare(session.Selected!, slot);
            var saved = session.Selected!;
            Sql("CREATE TRIGGER ticket_abort BEFORE INSERT ON character_dungeon_incount_extra BEGIN SELECT RAISE(ABORT,'quota rollback'); END");
            Reject<SqliteException>(() => repository.CommitDungeonIncountTicket(owner, plan, clock.GetUtcNow()), "末段额度SQL失败");
            That(repository.LoadCharacter(owner, 0).Revision == saved.Revision &&
                repository.LoadCharacter(owner, 0).Inventory.Find(slot) == saved.Inventory.Find(slot) && Usage() == default,
                "追加额度失败回滚扣券、revision、审计与冷却，不污染已用次数");
            Sql("DROP TRIGGER ticket_abort");
            foreach (var source in new[] { plan.Source with { Core = plan.Source.Core with { EquipmentLockId = 1 } },
                plan.Source with { Core = plan.Source.Core with { ExpireTime = (int)clock.GetUtcNow().ToUnixTimeSeconds() } } })
                Reject<InvalidDataException>(() => service.Prepare(saved with { Inventory = saved.Inventory with
                    { Items = saved.Inventory.Items.Replace(plan.Source, source) } }, slot), "锁定或过期券不消费");
            var used = session.UseDungeonIncountTicket(slot, service, repository);
            That(used.Outcome == ConsumableUseOutcome.Committed && used.Counts.All(count => count.Daily == 2 && count.Weekly == 3) &&
                Usage() == new DungeonIncountUsage(0, 0, 1, 1) && repository.LoadCharacter(owner, 0).Inventory.Find(slot) is null,
                "未耗尽也可预存日周各一次，扣一券与额度同事务");
            That(session.UseDungeonIncountTicket(slot, service, repository).Outcome == ConsumableUseOutcome.Rejected && Usage().DailyExtra == 1,
                "耗尽源槽重放不再加次");
            session.ReturnToRoster(); Select();
            That(!session.CanPublishConsumable(used.Guard), "同角色重选隔离旧成功回执");
            var period = DungeonIncountPolicy.Period(clock.GetUtcNow());
            var current = session.Selected!;
            for (int i = 0; i < 2; i++)
                That(repository.CommitDungeonIncountEntry(owner, current, row.CountKey, period, row, null) is not null,
                    "实际入场事务可消费基础和追加额度");
            That(repository.CommitDungeonIncountEntry(owner, current, row.CountKey, period, row, null) is null &&
                Usage() == new DungeonIncountUsage(2, 2, 1, 1), "追加额度恰好耗尽后拒绝，不重置已用次数");
            var old = service.Prepare(current, new(InventoryListType.Main, 66));
            clock.Advance(TimeSpan.FromSeconds(1));
            Reject<InvalidOperationException>(() => repository.CommitDungeonIncountTicket(owner, old, clock.GetUtcNow()), "跨日旧规划不得扣券加次");
            That(Usage() == new DungeonIncountUsage(0, 2, 0, 1) && DungeonIncountPolicy.CanEnter(row, Usage()),
                "06点只重置日额度和已用次数，未耗尽周追加保留");
            repository = new CharacterRepository(path);
            That(Usage() == new DungeonIncountUsage(0, 2, 0, 1), "重开库保留周追加，不读取成基础额度");
            var lost = new LostStore(repository);
            Reject<IOException>(() => session.UseDungeonIncountTicket(new(InventoryListType.Main, 66), service, lost), "实际提交后丢失结果");
            That(session.UseDungeonIncountTicket(new(InventoryListType.Main, 66), service, repository).Outcome == ConsumableUseOutcome.CloseConnection &&
                Usage() == new DungeonIncountUsage(0, 2, 1, 2), "提交不明门闩阻止再次扣券，存量读取只保留一次增量");
            session.End();
            session = new GameSession(repository, resources.Towns, owner, incounts: incounts, timeProvider: clock); Select();
            That(session.Selected!.Inventory.Find(new(InventoryListType.Main, 66)) is null &&
                session.EnterCounts().Where(count => row.Dungeons.Contains((int)count.Dungeon)).All(count => count.Daily == 2 && count.Weekly == 2),
                "重连投影真实已提交余额，不补扣或重复追加");
            clock.Advance(DateTimeOffset.Parse("2026-10-29T06:00:00+08:00") - clock.GetUtcNow());
            That(Usage() == default && DungeonIncountPolicy.DailyRemaining(row, Usage()) == 1 && DungeonIncountPolicy.WeeklyRemaining(row, Usage()) == 2,
                "周四06点日周追加与已用次数分别归零，不把旧额度带到新周");
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject<InvalidOperationException>(() => repository.CommitDungeonIncountTicket(owner, old, clock.GetUtcNow()), "旧owner不能扣券加次");
        }
        finally { session.End(); }

        // 代号希望追加券：资源形状为 8523 -1 -1，使用后日、周追加额度都必须增加。
        var hopeRow = incounts.Find(8523)!;
        using var hopeOwner = owners.Acquire("hope-incount-ticket", () => { });
        var hopeSlot = new InventorySlot(InventoryListType.Main,65);
        var hopeCandidate = CharacterCreation.Prepare("HopeTicket", resources.Characters.Get(0), resources.Towns.Room, resources.Towns.InitialReturn, null);
        repository.Create(hopeOwner, hopeCandidate with { Inventory = hopeCandidate.Inventory with { Items = hopeCandidate.Inventory.Items
            .Add(new(0, hopeSlot, new(10307551, ItemKind.Consumable, 1) { ExpireTime = 2145852000 })) } });
        var hopeSession = new GameSession(repository, resources.Towns, hopeOwner, incounts: incounts, timeProvider: clock);
        try
        {
            hopeSession.Select(0); hopeSession.MarkReady();
            That(resources.ItemResources.Items.ActionConsumable(10307551) is
                { Label: "[add dungeon incount item limit count]", Parameters: [8523, -1, -1] },
                "代号希望追加券完整动作按资源识别");
            var usedHope = hopeSession.UseDungeonIncountTicket(hopeSlot, service, repository);
            var hopeUsage = repository.LoadDungeonIncount(hopeOwner, hopeSession.Selected!.Id, hopeRow.CountKey,
                DungeonIncountPolicy.Period(clock.GetUtcNow(), incounts.WeeklyResetDay(hopeRow.CountKey)));
            That(usedHope.Outcome == ConsumableUseOutcome.Committed && hopeUsage.DailyExtra == 1 && hopeUsage.WeeklyExtra == 1 &&
                usedHope.Counts.Single(row => row.Dungeon == 8523) is { Daily: var daily, Weekly: var weekly } &&
                daily == checked((byte)(hopeRow.Limit + 1)) && weekly == checked((byte)(hopeRow.WeeklyLimit + 1)),
                "代号希望追加券同时增加日、周入场额度");
        }
        finally { hopeSession.End(); }
        // 黎明裂缝：基础 2 次，追加券 +1，重置券仅在耗尽后清空已用和追加额度。
        var dawnRow = incounts.Find(530)!;
        using var dawnOwner = owners.Acquire("dawn-incount-ticket", () => { });
        var addSlot = new InventorySlot(InventoryListType.Main, 70);
        var resetSlot = new InventorySlot(InventoryListType.Main, 71);
        var dawnCandidate = CharacterCreation.Prepare("DawnTicket", resources.Characters.Get(0), resources.Towns.Room, resources.Towns.InitialReturn, null);
        repository.Create(dawnOwner, dawnCandidate with
        {
            Inventory = dawnCandidate.Inventory with
            {
                Items = dawnCandidate.Inventory.Items
                    .Add(new(0, addSlot, new(10306646, ItemKind.Consumable, 1) { ExpireTime = 2145852000 }))
                    .Add(new(0, resetSlot, new(10308584, ItemKind.Consumable, 1) { ExpireTime = 2145852000 }))
            }
        });
        var dawn = repository.LoadCharacter(dawnOwner, 0);
        var addPlan = service.Prepare(dawn, addSlot);
        var added = repository.CommitDungeonIncountTicket(dawnOwner, addPlan, clock.GetUtcNow());
        That(added.Usage.DailyExtra == 1 && DungeonIncountPolicy.DailyRemaining(dawnRow, added.Usage) == 3,
            "黎明裂缝追加券只增加 1 次日入场");
        var blockedReset = service.Prepare(added.Character, resetSlot);
        Reject<InvalidOperationException>(() => repository.CommitDungeonIncountTicket(dawnOwner, blockedReset, clock.GetUtcNow()),
            "黎明裂缝还有剩余次数时重置券拒绝使用");
        var currentDawn = added.Character;
        var dawnPeriod = DungeonIncountPolicy.Period(clock.GetUtcNow(), incounts.WeeklyResetDay(dawnRow.CountKey));
        for (int i = 0; i < 3; i++)
            currentDawn = repository.CommitDungeonIncountEntry(dawnOwner, currentDawn, dawnRow.CountKey, dawnPeriod, dawnRow, null)!.Character;
        var exhaustedDawn = repository.LoadCharacter(dawnOwner, 0);
        var resetPlan = service.Prepare(exhaustedDawn, resetSlot);
        var reset = repository.CommitDungeonIncountTicket(dawnOwner, resetPlan, clock.GetUtcNow());
        That(reset.Usage == default && DungeonIncountPolicy.DailyRemaining(dawnRow, reset.Usage) == 2,
            "黎明裂缝重置券耗尽后恢复初始 2 次并清空追加额度");

        Console.WriteLine($"PASS dungeon incount ticket {Count - before}");

        void Select() { session.Select(0); session.MarkReady(); }
        DungeonIncountUsage Usage() => repository.LoadDungeonIncount(owner, session.Selected!.Id, row.CountKey, DungeonIncountPolicy.Period(clock.GetUtcNow()));
        void Sql(string sql) { using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    }

    sealed class LostStore(IDungeonIncountTicketStore actual) : IDungeonIncountTicketStore
    {
        public DungeonIncountCommit CommitDungeonIncountTicket(AccountOwners.Lease owner, DungeonIncountTicketPlan plan, DateTimeOffset now)
        { actual.CommitDungeonIncountTicket(owner, plan, now); throw new IOException("lost result"); }
    }
}
