using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Auth;
using DfoServer.Testing;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.Events;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class BattlePassMissionTests
{
    static void EventControlPersistence()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pass-control-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool succeeded = false;
        try
        {
            string path = Path.Combine(root, "game.db");
            var now = Start.AddDays(58).AddHours(1);
            var clock = new MissionClock(now);
            var repository = new CharacterRepository(path);
            var events = new ServerEventService(new ServerEventRepository(path), clock);
            // 此处故意模拟已持久关闭、Changed尚未发布的旧true缓存，事务仍须拒绝附带进度。
            var policy = new BattlePassMissionPolicy("control-season", Start, Catalog(), _ => true, false);
            events.ConfigureBattlePass(policy.Start, policy.End, false);
            repository.ConfigureBattlePassMissions(policy, clock);
            events.InitializeDefault(2520);
            var adventure = AdventureCoreTests.SyntheticCatalog();
            var service = new AdventureExpeditionService(adventure, AdventureExpeditionTests.SyntheticCatalog());
            new AuthenticationService(new AuthenticationRepository(path)).Create("pass-control", "fixture-password");
            using var owner = new AccountOwners().Acquire("pass-control", () => { });
            var role = repository.Create(owner, new NewCharacter("PassControl", new Location(1, 0, 10, 10), null) { GrowType = 33, Level = 95 });
            role = repository.CommitInventory(owner, role, new InventoryPlan(role.Inventory,
                CurrencyWallet.Set(role.Inventory, CurrencyKind.Cera, 10000), "fixture_credit", new(role)));
            repository.InitializeAdventure(owner, adventure, now);
            var initial = repository.ReadBattlePassMissions(owner, now)!;
            That(initial.Active, "活动开启有真实26槽，初始无伪进度");
            events.Override(2520, false);
            That(!repository.ReadBattlePassMissions(owner, now)!.Active, "缓存旧true时只读快照仍按同事务off隐藏任务");
            var before = repository.ReadAdventure(owner, adventure, now);
            var plan = service.Start(before, 1, 21600, [(0, 0, 1)], now).Plan! with { ActorId = role.Id };
            var running = repository.CommitAdventureExpedition(owner, plan, adventure, now);
            using (var db = new SqliteGameDatabase(path).Open())
            {
                using var query = db.CreateCommand(); query.CommandText = "SELECT count(*) FROM account_battle_pass_source_events";
                That((long)query.ExecuteScalar()! == 0 && repository.ReadBattlePass(owner, policy.SeasonKey).Revision == 0,
                    "关闭期间原远征提交成功，战令来源、进度及版本保持不变");
            }
            running = repository.CommitAdventureExpedition(owner,
                service.Finish(running, 1, false, now).Plan! with { ActorId = role.Id }, adventure, now);
            events.Override(2520, true);
            plan = service.Start(running, 1, 21600, [(0, 0, 1)], now).Plan! with { ActorId = role.Id };
            repository.CommitAdventureExpedition(owner, plan, adventure, now);
            var ready = repository.ReadBattlePassMissions(owner, now)!;
            That(ready.Slots.Single(row => row.Instance.Key.Id == 12).Ready && ready.Season.Revision == 1,
                "同一冻结季重新开启后真实新来源正常推进，未补发关闭期间历史");
            events.Override(2520, false);
            var denied = repository.CommitBattlePassMission(owner, role, ready.Season.Revision, now, 1, 12, 0, 0, clock);
            That(!denied.Accepted && repository.ReadBattlePass(owner, policy.SeasonKey).TotalExperience == 0,
                "关闭后已有待领取任务也不能提交XP，原达标进度保留");
            var product = new CashShopProduct(9001, 14, 1, 600, "item");
            var paid = CurrencyWallet.Set(role.Inventory, CurrencyKind.Cera, 9400);
            var payment = CashShopPaymentFact.Capture(role.Inventory, role.Inventory, paid, paid,
                [new(product, true)], 600, 0, 0, now, clock);
            role = repository.CommitInventory(owner, role, new InventoryPlan(role.Inventory, paid, "cash_shop_buy", new(role))
                { CashShopPayment = payment });
            That(repository.ReadBattlePass(owner, policy.SeasonKey).TotalExperience == 0 &&
                CurrencyWallet.Get(role.Inventory, CurrencyKind.Cera) == 9400,
                "关闭时普通商城真实支付仍完成，同事务不附加消费XP");
            var certificateProduct = new CashShopProduct(105659, 490705043, 1, 4900, "package");
            var certificateAfter = CurrencyWallet.Set(role.Inventory, CurrencyKind.Cera, 4500);
            var certificateFact = CashShopPaymentFact.Capture(role.Inventory, role.Inventory, certificateAfter, certificateAfter,
                [new(certificateProduct, true)], 4900, 0, 0, now, clock);
            var certificate = new BattlePassCertificatePlan(repository.ReadBattlePass(owner, policy.SeasonKey),
                new InventoryPlan(role.Inventory, certificateAfter, "cash_shop_buy", new(role)) { CashShopPayment = certificateFact },
                new CashShopPurchaseRequest(0, [new(105659, 0)]), new CashShopPurchaseReceipt([certificateProduct],
                    new Dictionary<int, IReadOnlyList<CashShopBonus>>()), Guid.NewGuid(), now, clock);
            Reject<BattlePassCertificateChangedException>(() => repository.CommitBattlePassCertificate(owner, role, certificate, now),
                "缓存旧true时资格证仍被writer内2520关闭拒绝，不扣费或写资格");
            That(repository.ReadBattlePass(owner, policy.SeasonKey).PassType == 0 &&
                CurrencyWallet.Get(repository.List(owner).Single(row => row.Id == role.Id).Inventory, CurrencyKind.Cera) == 9400, "资格证关闭拒绝保留钱包与账号季资格");
            events.Override(2520, true);
            var purchased = repository.CommitBattlePassCertificate(owner, role, certificate, now);
            That(purchased.State.Season is { PassType: 1, TotalExperience: 2450 } &&
                purchased.State.Slots.Single(row => row.Instance.Key.Id == 12).Ready &&
                purchased.State.Season.SeasonKey == policy.SeasonKey,
                "重新开启沿原季同一合法计划正常扣费和授予资格/消费XP，既有达标进度保留");
            succeeded = true;
        }
        finally
        {
            if (succeeded) TestFiles.DeleteDirectory(root, recursive: true);
            else Console.Error.WriteLine($"Battle-pass control fixture preserved: {root}");
        }
    }
}
