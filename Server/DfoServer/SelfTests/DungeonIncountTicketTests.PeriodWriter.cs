using System.Reflection;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonIncountTicketTests
{
    static void RunPeriodWriter(TcpTestResources resources, string root)
    {
        foreach (int day in new[] { 5, 6 })
        {
            string path = Path.Combine(root, "ticket-writer-" + day + ".db");
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("ticket-writer", () => { });
            var clock = new ConsumableUseSessionTests.FakeClock();
            clock.Advance(DateTimeOffset.Parse($"2026-11-{day:00}T05:59:59+08:00") - clock.GetUtcNow());
            var slot = new InventorySlot(InventoryListType.Main, 65);
            var candidate = CharacterCreation.Prepare("PeriodWriter", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, null);
            var character = repository.Create(owner, candidate with { Level = 95, Inventory = candidate.Inventory with
            { Items = candidate.Inventory.Items.Add(new(0, slot, new(10306644, ItemKind.Consumable, 1))) } });
            var counts = DungeonIncountCatalog.Load(resources.Archive);
            var limits = ClearRewardLimitCatalog.Load(resources.Archive);
            var service = new DungeonIncountTicketService(resources.ItemResources.Items, counts, resources.Towns, clock, limits);
            var plan = service.Prepare(character, slot, 214);
            string before = ClientSettingsSnapshots.Capture(path, true);
            var gate = (SemaphoreSlim)typeof(SqliteWriteGate).GetField("Gate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var waiters = typeof(SemaphoreSlim).GetField("m_waitCount", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? typeof(SemaphoreSlim).GetField("_waitCount", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("无法观察原写入闸门等待计数");
            Exception? failure = null;
            using var finished = new ManualResetEventSlim();
            var worker = new Thread(() =>
            {
                try { repository.CommitDungeonIncountTicket(owner, plan, clock.GetUtcNow()); }
                catch (Exception error) { failure = error; }
                finally { finished.Set(); }
            }) { IsBackground = true, Name = "ticket-period-writer" };
            bool started = false;
            try
            {
                using var db = new SqliteConnection("Data Source=" + path); db.Open();
                using var write = db.BeginWrite("DungeonIncountTicketTests.PeriodHold");
                worker.Start(); started = true;
                That(SpinWait.SpinUntil(() => Convert.ToInt32(waiters.GetValue(gate)) > 0 || finished.IsSet, TimeSpan.FromSeconds(5)) &&
                    !finished.IsSet, "永久泰波尔斯券实际等待原写入闸门");
                clock.Advance(TimeSpan.FromSeconds(1));
            }
            finally
            {
                if (started && (!finished.Wait(TimeSpan.FromSeconds(10)) || !worker.Join(TimeSpan.FromSeconds(5))))
                    throw new TimeoutException("释放写入闸门后追加券未及时退出");
            }
            That(failure is InventoryQualificationExpiredException && ClientSettingsSnapshots.Capture(path, true) == before,
                "写入排队跨日/周06点整笔回滚，券、冷却、额度、revision和审计均保持");
            var used = repository.CommitDungeonIncountTicket(owner, service.Prepare(character, slot, 214), clock.GetUtcNow());
            repository = new CharacterRepository(path);
            That(used.Character.Inventory.Find(slot) is null &&
                repository.LoadDungeonIncount(owner, character.Id, plan.Row!.CountKey, DungeonIncountPolicy.Period(clock.GetUtcNow())).DailyExtra == 1,
                "跨界拒绝后可重新规划一次使用，并在新周期存档恢复");
        }
    }
}
