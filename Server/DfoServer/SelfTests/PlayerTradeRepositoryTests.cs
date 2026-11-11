using System.Reflection;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Trade;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PlayerTradeRepositoryTests
{
    public static void RunCore()
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-player-trade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var database = new SqliteGameDatabase(path);
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var firstOwner = owners.Acquire("player-trade-a", () => { });
            using var secondOwner = owners.Acquire("player-trade-b", () => { });
            var first = repository.Create(firstOwner, new("TradeA", new(38, 1, 450, 234, 0), null)
            { Inventory = new([new(0, new(InventoryListType.Main, 121), new(1001, ItemKind.Material, 3))], Gold: 1000) });
            var second = repository.Create(secondOwner, new("TradeB", new(38, 1, 450, 234, 0), null)
            { Inventory = new([new(0, new(InventoryListType.Main, 122), new(1002, ItemKind.Material, 4))], Gold: 200) });
            var firstPlan = new InventoryPlan(first.Inventory, first.Inventory with { Gold = 900 }, "player_trade");
            var secondPlan = new InventoryPlan(second.Inventory, second.Inventory with { Gold = 300 }, "player_trade");
            var unbalanced = new InventoryPlan(second.Inventory, second.Inventory with { Gold = 301 }, "player_trade");
            Reject<InvalidDataException>(() => repository.CommitPlayerTrade(firstOwner, secondOwner, first, second, firstPlan, unbalanced),
                "不守恒的金币交易不能提交");
            var injected = new InventoryPlan(second.Inventory, secondPlan.After, "player_trade") { ExpBonus = (100, 1100) };
            Reject<InvalidDataException>(() => repository.CommitPlayerTrade(firstOwner, secondOwner, first, second, firstPlan, injected),
                "交易计划不能携带无关经验变更");
            Sql($"CREATE TRIGGER fail_second_trade BEFORE UPDATE ON characters WHEN NEW.id={second.Id} BEGIN SELECT RAISE(ABORT, 'second trade write'); END");
            Reject<SqliteException>(() => repository.CommitPlayerTrade(firstOwner, secondOwner, first, second, firstPlan, secondPlan),
                "第二方写入失败必须回滚第一方");
            var unchangedFirst = repository.LoadCharacter(firstOwner, first.Slot);
            var unchangedSecond = repository.LoadCharacter(secondOwner, second.Slot);
            That(unchangedFirst.Inventory.Gold == 1000 && unchangedFirst.Revision == first.Revision &&
                unchangedSecond.Inventory.Gold == 200 && unchangedSecond.Revision == second.Revision,
                "第二方失败后双方金币和版本不变");
            Sql("DROP TRIGGER fail_second_trade");
            var (savedFirst, savedSecond) = repository.CommitPlayerTrade(firstOwner, secondOwner, first, second, firstPlan, secondPlan);
            That(savedFirst.Inventory.Gold == 900 && savedSecond.Inventory.Gold == 300 &&
                repository.LoadCharacter(firstOwner, first.Slot).Inventory.Gold == 900 &&
                repository.LoadCharacter(secondOwner, second.Slot).Inventory.Gold == 300,
                "双方金币在同一事务中落库");
            Reject<InvalidOperationException>(() => repository.CommitPlayerTrade(firstOwner, secondOwner, first, second, firstPlan, secondPlan),
                "旧角色快照不能重放交易");

            var firstItem = savedFirst.Inventory.Items.Single();
            var secondItem = savedSecond.Inventory.Items.Single();
            var exchangeFirst = new InventoryPlan(savedFirst.Inventory,
                savedFirst.Inventory with { Items = [secondItem with { Position = firstItem.Position }] }, "player_trade");
            var exchangeSecond = new InventoryPlan(savedSecond.Inventory,
                savedSecond.Inventory with { Items = [firstItem with { Position = secondItem.Position }] }, "player_trade");
            var firstTransfers = new HashSet<long> { firstItem.Uid };
            var secondTransfers = new HashSet<long> { secondItem.Uid };
            Reject<InvalidDataException>(() => repository.CommitPlayerTrade(firstOwner, secondOwner, savedFirst, savedSecond,
                exchangeFirst, exchangeSecond), "未授权物品不能转移");
            Sql($"CREATE TRIGGER fail_second_item_trade BEFORE UPDATE ON characters WHEN NEW.id={second.Id} BEGIN SELECT RAISE(ABORT, 'second trade write'); END");
            Reject<SqliteException>(() => repository.CommitPlayerTrade(firstOwner, secondOwner, savedFirst, savedSecond,
                exchangeFirst, exchangeSecond, firstTransfers, secondTransfers), "物品交易第二方写入失败必须回滚");
            That(repository.LoadCharacter(firstOwner, first.Slot).Inventory.Items.Single() == firstItem &&
                repository.LoadCharacter(secondOwner, second.Slot).Inventory.Items.Single() == secondItem,
                "物品交易失败后双方槽位、实例与所有权不变");
            Sql("DROP TRIGGER fail_second_item_trade");
            var (exchangedFirst, exchangedSecond) = repository.CommitPlayerTrade(firstOwner, secondOwner, savedFirst, savedSecond,
                exchangeFirst, exchangeSecond, firstTransfers, secondTransfers);
            That(exchangedFirst.Inventory.Items.Single().Uid == secondItem.Uid &&
                exchangedSecond.Inventory.Items.Single().Uid == firstItem.Uid &&
                repository.LoadCharacter(firstOwner, first.Slot).Inventory.Items.Single() == exchangedFirst.Inventory.Items.Single() &&
                repository.LoadCharacter(secondOwner, second.Slot).Inventory.Items.Single() == exchangedSecond.Inventory.Items.Single(),
                "双方物品实例在同一事务中交换");
            var splitItem = exchangedFirst.Inventory.Items.Single();
            var ticket = new TradeTicket(new(exchangedFirst.Id, firstOwner.Generation, 1), new(exchangedSecond.Id, secondOwner.Generation, 1),
                new(10, [new(3, splitItem, 2)]), new(0, []));
            var splitPlan = TradeSettlement.Plan(exchangedFirst, exchangedSecond, ticket)!;
            var forged = new InventoryPlan(splitPlan.Second.Before, splitPlan.Second.After with { Gold = splitPlan.Second.After.Gold + 1 }, "player_trade");
            Reject<InvalidDataException>(() => repository.CommitPlayerTrade(firstOwner, secondOwner, exchangedFirst, exchangedSecond,
                splitPlan.First, forged, ticket: ticket), "冻结报价不能替换为伪造的成交计划");
            Sql($"CREATE TRIGGER fail_split_trade BEFORE UPDATE ON characters WHEN NEW.id={second.Id} BEGIN SELECT RAISE(ABORT, 'split trade write'); END");
            Reject<SqliteException>(() => repository.CommitPlayerTrade(firstOwner, secondOwner, exchangedFirst, exchangedSecond,
                splitPlan.First, splitPlan.Second, ticket: ticket), "拆分交易第二方写入失败必须回滚");
            That(repository.LoadCharacter(firstOwner, first.Slot).Inventory.Items.Single() == splitItem &&
                repository.LoadCharacter(secondOwner, second.Slot).Inventory.Items.Single().Uid == firstItem.Uid,
                "拆分交易失败后双方实例和槽位不变");
            Sql("DROP TRIGGER fail_split_trade");
            var (splitFirst, splitSecond) = repository.CommitPlayerTrade(firstOwner, secondOwner, exchangedFirst, exchangedSecond,
                splitPlan.First, splitPlan.Second, ticket: ticket);
            That(splitFirst.Inventory.Items.Single().Core.Count == 2 &&
                splitSecond.Inventory.Items.Single(item => item.Core.ItemId == splitItem.Core.ItemId).Core.Count == 2 &&
                splitSecond.Inventory.Items.Single(item => item.Core.ItemId == splitItem.Core.ItemId).Uid != 0,
                "拆分交易提交后两侧数量守恒且新实例已分配 UID");

            void Sql(string statement)
            {
                using var connection = database.Open();
                using var command = connection.CreateCommand();
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestFiles.DeleteDirectory(root, true);
        }
        PeriodDeadline();
    }

    static void PeriodDeadline()
    {
        SuccessBeforeDeadline();
        CrossDeadline(expireFirst: true);
        CrossDeadline(expireFirst: false);
    }

    static void SuccessBeforeDeadline()
    {
        var start = DateTimeOffset.UtcNow;
        var clock = new MutableClock(start);
        using var scene = new DeadlineScene(clock, start.AddHours(2), start.AddHours(2));
        var (first, second) = WhileWriterQueued(scene.DatabasePath, null, () => scene.Commit());
        var loadedFirst = scene.Repository.LoadCharacter(scene.FirstOwner, first.Slot);
        var loadedSecond = scene.Repository.LoadCharacter(scene.SecondOwner, second.Slot);
        That(first.Inventory.Gold == 1500 && second.Inventory.Gold == 1500 &&
            first.Inventory.Items.Single().Uid == scene.SecondItem.Uid && first.Inventory.Items.Single().ExpireTime == scene.SecondItem.ExpireTime &&
            second.Inventory.Items.Single().Uid == scene.FirstItem.Uid && second.Inventory.Items.Single().ExpireTime == scene.FirstItem.ExpireTime &&
            loadedFirst.Inventory.Gold == 1500 && loadedFirst.Inventory.Items.Single().Uid == scene.SecondItem.Uid &&
            loadedSecond.Inventory.Gold == 1500 && loadedSecond.Inventory.Items.Single().Uid == scene.FirstItem.Uid &&
            loadedFirst.Revision == first.Revision && loadedSecond.Revision == second.Revision,
            "writer 排队但尚未跨截止时，双方金币和期限实例一起成交并重读一致");
        scene.Integrity();
    }

    static void CrossDeadline(bool expireFirst)
    {
        var start = DateTimeOffset.UtcNow;
        var clock = new MutableClock(start);
        var soon = start.AddMinutes(30);
        var later = start.AddHours(2);
        using var scene = new DeadlineScene(clock, expireFirst ? soon : later, expireFirst ? later : soon);
        string before = scene.Snapshot();
        var rejected = WhileWriterQueued(scene.DatabasePath, () => clock.Set(start.AddHours(1)), () =>
        {
            try { scene.Commit(); return null; }
            catch (InventoryQualificationExpiredException error) { return error; }
        });
        That(rejected is not null && scene.Snapshot() == before, expireFirst
            ? "排队跨过第一方转出截止时整笔取消，双方金币、实例和托管不变"
            : "排队跨过第二方转出截止时整笔取消，双方金币、实例和托管不变");
        That(RejectCommit(scene) is not null && scene.Snapshot() == before, "过期后重试同一报价仍然取消且不写库");
        clock.Set(start);
        var (first, second) = WhileWriterQueued(scene.DatabasePath, null, () => scene.Commit());
        That(first.Inventory.Gold == 1500 && second.Inventory.Gold == 1500 &&
            first.Inventory.Items.Single().Uid == scene.SecondItem.Uid && second.Inventory.Items.Single().Uid == scene.FirstItem.Uid &&
            scene.Repository.LoadCharacter(scene.FirstOwner, first.Slot).Inventory.Items.Single().Uid == scene.SecondItem.Uid &&
            scene.Repository.LoadCharacter(scene.SecondOwner, second.Slot).Inventory.Items.Single().Uid == scene.FirstItem.Uid,
            "时钟回到截止前，同一冻结报价可以重新成交");
        scene.Integrity();
    }

    static InventoryQualificationExpiredException? RejectCommit(DeadlineScene scene)
    {
        try { scene.Commit(); return null; }
        catch (InventoryQualificationExpiredException error) { return error; }
    }

    static T WhileWriterQueued<T>(string path, Action? whileQueued, Func<T> action)
    {
        T result = default!;
        Exception? failure = null;
        using var finished = new ManualResetEventSlim();
        var worker = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception error) { failure = error; }
            finally { finished.Set(); }
        }) { IsBackground = true, Name = "trade-deadline-writer" };
        var gate = (SemaphoreSlim)typeof(SqliteWriteGate).GetField("Gate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var waiters = typeof(SemaphoreSlim).GetField("m_waitCount", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? typeof(SemaphoreSlim).GetField("_waitCount", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("无法观察原写入闸门等待计数");
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
        db.Open();
        bool started = false;
        try
        {
            var write = db.BeginWrite("PlayerTradeRepositoryTests.DeadlineHold");
            try
            {
                var tx = write.Transaction;
                try
                {
                    worker.Start();
                    started = true;
                    That(SpinWait.SpinUntil(() => Convert.ToInt32(waiters.GetValue(gate)) > 0 || finished.IsSet, TimeSpan.FromSeconds(5)) &&
                        !finished.IsSet, "交易实际等待原写入闸门");
                    whileQueued?.Invoke();
                    That(!finished.IsSet, "调整时钟时交易仍堵在 writer 上");
                }
                finally { tx.Dispose(); }
            }
            finally { write.Dispose(); }
            if (started && (!finished.Wait(TimeSpan.FromSeconds(10)) || !worker.Join(TimeSpan.FromSeconds(5))))
                throw new TimeoutException("释放写入闸门后交易未退出");
        }
        finally { db.Dispose(); }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        long ticks = start.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Set(DateTimeOffset value) => Interlocked.Exchange(ref ticks, value.UtcTicks);
    }

    sealed class DeadlineScene : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "s5a9-trade-deadline-" + Guid.NewGuid().ToString("N"));
        readonly AccountOwners owners = new();
        public string DatabasePath { get; }
        public CharacterRepository Repository { get; }
        public AccountOwners.Lease FirstOwner { get; }
        public AccountOwners.Lease SecondOwner { get; }
        public Character First { get; }
        public Character Second { get; }
        public InventoryItem FirstItem { get; }
        public InventoryItem SecondItem { get; }
        readonly TradeTicket ticket;
        readonly TradeSettlementPlan plan;
        readonly ItemCatalog items;
        public DeadlineScene(TimeProvider clock, DateTimeOffset firstExpires, DateTimeOffset secondExpires)
        {
            DatabasePath = System.IO.Path.Combine(root, "game.db");
            Directory.CreateDirectory(root);
            Repository = new(DatabasePath, promotionClock: clock);
            FirstOwner = owners.Acquire("trade-deadline-a", () => { });
            SecondOwner = owners.Acquire("trade-deadline-b", () => { });
            var core = new ItemCore(50002, ItemKind.Equipment, 999999998);
            First = Repository.Create(FirstOwner, new("DeadlineA", new(38, 1, 450, 234, 0), null)
            {
                Inventory = new([new(0, new(InventoryListType.Main, 9), core with { ExpireTime = checked((int)firstExpires.ToUnixTimeSeconds()) })], Gold: 1000)
            });
            Second = Repository.Create(SecondOwner, new("DeadlineB", new(38, 1, 450, 234, 0), null)
            {
                Inventory = new([new(0, new(InventoryListType.Main, 9), core with { ExpireTime = checked((int)secondExpires.ToUnixTimeSeconds()) })], Gold: 2000)
            });
            FirstItem = First.Inventory.Items.Single();
            SecondItem = Second.Inventory.Items.Single();
            items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
            items.PrefillTemplates(new Dictionary<int, ItemTemplate>
            {
                [50002] = new(50002, ItemKind.Equipment, 12, 1, 0, 1, 0, 0, 1, "[free]", 65535, 0, "期限") { Expiration = new(Days: 30) }
            });
            ticket = new(new(First.Id, FirstOwner.Generation, 1), new(Second.Id, SecondOwner.Generation, 1),
                new(0, [new(0, FirstItem, 1)]), new(500, [new(0, SecondItem, 1)]));
            plan = TradeSettlement.Plan(First, Second, ticket, items) ?? throw new InvalidOperationException("截止前的期限交易必须能规划");
        }
        public (Character First, Character Second) Commit() =>
            Repository.CommitPlayerTrade(FirstOwner, SecondOwner, First, Second, ticket, plan, items);
        public string Snapshot()
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            var tables = new List<string>();
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using (var reader = command.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));
            var rows = new List<object>();
            foreach (string table in tables)
            {
                command.CommandText = "SELECT * FROM [" + table + "] ORDER BY rowid";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    rows.Add(new { Table = table, Values = Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue).ToArray() });
            }
            return JsonSerializer.Serialize(rows);
        }
        public void Integrity()
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "PRAGMA integrity_check";
            That(command.ExecuteScalar() is "ok", "期限交易提交后库完整");
            command.CommandText = "SELECT count(*) FROM pragma_foreign_key_check";
            That(command.ExecuteScalar() is 0L, "期限交易提交后外键完整");
        }
        public void Dispose()
        {
            SecondOwner.Dispose();
            FirstOwner.Dispose();
            SqliteConnection.ClearAllPools();
            TestFiles.DeleteDirectory(root, true);
        }
    }
}
