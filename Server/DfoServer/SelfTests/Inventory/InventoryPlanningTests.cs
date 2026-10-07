using DfoServer.Infrastructure;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;

namespace DfoServer.Testing.Inventory;

internal static class InventoryPlanningTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources itemResources, Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-planning-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var owners = new AccountOwners();
            using var first = owners.Acquire("planning-first", () => { });
            using var second = owners.Acquire("planning-second", () => { });
            var catalog = itemResources.Items;
            var booster = new InventoryBoosterService(catalog, itemResources.Boosters, itemResources.GoldLimits, itemResources.EpicPieces, new Random(31024));
            repository.Create(first, new("Planner", towns.Room, towns.InitialReturn)
            {
                Inventory = new([new(0, new(InventoryListType.Main, 3), InventoryRewardGrantService.Create(catalog.Get(2058), 1024))])
            });
            repository.Create(second, new("Observer", towns.Room, towns.InitialReturn)
            {
                Inventory = new([new(0, new(InventoryListType.Main, 3), InventoryRewardGrantService.Create(catalog.Get(7542), 2))])
            });
            var session = new GameSession(repository, towns, first);
            var other = new GameSession(repository, towns, second);
            int calls = 0;
            check(!session.PlanAndCommitInventory(_ => { calls++; return InventoryResult.Reject(19); }).Accepted && calls == 0,
                "unselected session does not run a planner");
            session.Select(0);
            check(!session.PlanAndCommitInventory(_ => { calls++; return InventoryResult.Reject(19); }).Accepted && calls == 0,
                "loading session does not run a planner");
            session.MarkReady();
            other.Select(0);
            other.MarkReady();

            InventoryResult? otherResult = null;
            var outcome = DuringPlan(() => otherResult = other.MutateInventory(role => booster.Open(role, new(3, 1), out _)));
            check(otherResult is { Accepted: true } && outcome.Result is { Accepted: true } && outcome.Error is null,
                "another account commits while bulk planning is paused outside its account lock");
            check(JsonSerializer.Serialize(repository.List(first).Single()) == JsonSerializer.Serialize(session.Selected) &&
                JsonSerializer.Serialize(repository.List(second).Single()) == JsonSerializer.Serialize(other.Selected),
                "concurrent accounts keep separate committed item snapshots");

            string before = Assets();
            outcome = DuringPlan(() => session.UpdateProgress(new(CharacterProgressField.Level, first.Account, "Planner", 2, 0)));
            check(outcome.Result is { Accepted: false, Error: 19 } && outcome.Error is null && Assets() == before && session.Selected!.Level == 2,
                "GM progress change invalidates an old plan even without an inventory revision change");

            outcome = DuringPlan(() => { session.ReturnToRoster(); session.Select(0); session.MarkReady(); });
            check(outcome.Result is { Accepted: false, Error: 19 } && outcome.Error is null && Assets() == before,
                "returning to the same character invalidates an old selection epoch");

            string? afterOtherCommit = null;
            outcome = DuringPlan(() =>
            {
                session.MutateInventory(role => InventoryRewardGrantService.TryGrantBatch(role, [new(1106, 1)], catalog, itemResources.GoldLimits, itemResources.EpicPieces));
                afterOtherCommit = Assets();
            });
            check(outcome.Result is { Accepted: false, Error: 19 } && outcome.Error is null && Assets() == afterOtherCommit,
                "another committed inventory mutation is never overwritten or automatically rerolled");

            AccountOwners.Lease? replacement = null;
            try
            {
                string saved = JsonSerializer.Serialize(repository.List(first));
                outcome = DuringPlan(() => replacement = owners.Acquire(first.Account, () => { }));
                check(outcome.Result is null && outcome.Error is InvalidOperationException &&
                    JsonSerializer.Serialize(repository.List(replacement!)) == saved,
                    "owner replacement during planning forbids commit and leaves the replacement's database unchanged");
            }
            finally { replacement?.Dispose(); }

            string Assets() => JsonSerializer.Serialize(session.Selected!.Inventory);
            (InventoryResult? Result, Exception? Error) DuringPlan(Action concurrent)
            {
                using var entered = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                InventoryResult? result = null;
                Exception? error = null;
                int planned = 0;
                var pending = Task.Factory.StartNew(() =>
                {
                    try
                    {
                        result = session.PlanAndCommitInventory(role =>
                        {
                            planned++;
                            var plan = booster.Open(role, new(3, 128), out _);
                            entered.Set();
                            if (!release.Wait(TimeSpan.FromSeconds(10)))
                                throw new TimeoutException("Planning barrier was not released");
                            return plan;
                        }, 19);
                    }
                    catch (Exception exception) { error = exception; }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Task? competing = null;
                try
                {
                    check(entered.Wait(TimeSpan.FromSeconds(5)), "planner reaches deterministic pause");
                    competing = Task.Factory.StartNew(concurrent, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    check(competing.Wait(TimeSpan.FromSeconds(3)), "unrelated owner operation completes before planner resumes");
                }
                finally
                {
                    release.Set();
                    if (!pending.Wait(TimeSpan.FromSeconds(5)) || competing is not null && !competing.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Planning regression left a worker running");
                }
                check(planned == 1, "a conflict never replays the random planning callback");
                return (result, error);
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
