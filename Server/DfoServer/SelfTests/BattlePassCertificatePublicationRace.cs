using System.Buffers.Binary;
using System.Net;
using DfoServer.Development;
using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers.Inventory;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassCertificatePublicationRace
{
    internal static async Task RunAsync(TcpTestResources resources)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pass-certificate-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await DeluxeSyntheticScenario();
            foreach (bool deluxe in new[] { false, true })
            foreach (string change in new[] { "unchanged", "reselect", "takeover", "recharge", "recharge-twice" })
                await Scenario(change, deluxe);
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }

        async Task DeluxeSyntheticScenario()
        {
            const string season = "certificate-deluxe-synthetic";
            var items = resources.ItemResources;
            var (policy, now) = BattlePassMissionTests.AckFixturePolicy(season);
            var clock = new Clock(now);
            string path = Path.Combine(root, "deluxe-synthetic.db");
            var repository = new CharacterRepository(path);
            repository.ConfigureBattlePassMissions(policy);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("certificate-deluxe", () => { });
            var created = repository.Create(owner, new NewCharacter("DeluxeSyn", resources.Towns.Room, resources.Towns.InitialReturn)
                { Level = 95, AcceptedStoryLevel = 95 });
            var grant = new CurrencyService(items.GoldLimits).Grant(created, CurrencyKind.Cera, 30000);
            InventoryService.Commit(repository, owner, created, grant);
            var session = new GameSession(repository, resources.Towns, owner, timeProvider: clock);
            session.Select(0); session.MarkReady();
            _ = repository.ReadBattlePassMissions(owner, now);
            var synthetic = CashShopCatalog.Parse($"""
                [package]
                105659 490705043 1 0 4900 0 0 0 0 0 0
                105658 490705044 1 0 15600 0 0 0 0 0 0
                [/package]
                [buy only cera]
                490705043
                490705044
                [/buy only cera]
                """);
            var offer = BattlePassDeluxePurchase.Resolve(synthetic);
            That(offer is { CommodityId: 105658, ItemId: 490705044, CeraPrice: 15600 } &&
                items.Items.Get(BattlePassDeluxePurchase.Item).IsPremiumService,
                "豪华合成GREEN使用真实044模板和CashShopCatalog商品映射");
            var cash = new InventoryCashShopService(items.Items, synthetic, items.GoldLimits, items.EpicPieces, items.Cargo,
                contracts: items.Contracts, avatars: items.AvatarPackages, clock: clock, deluxeOffer: offer);
            long beforeCera = CurrencyWallet.Get(session.Selected!.Inventory, CurrencyKind.Cera);
            var result = session.BuyCashShop(new(0, [new(105658, 0)]), cash, null,
                out var receipt, out _, out var certificate);
            var state = repository.ReadBattlePassMissions(owner, now)!;
            long afterCera = CurrencyWallet.Get(session.Selected!.Inventory, CurrencyKind.Cera);
            long threshold = checked((long)policy.Catalog.TotalExperienceToReach(BattlePassDeluxePurchase.TargetLevel));
            That(result.Accepted && receipt is not null && certificate is not null && beforeCera - afterCera == 15600 &&
                state.Season.PassType == 1 && state.Season.TotalExperience >= threshold &&
                Number(path, "SELECT count(*) FROM account_battle_pass_deluxe_orders") == 1 &&
                session.Selected.Inventory.Items.All(row => row.Core.ItemId != BattlePassDeluxePurchase.Item),
                "豪华合成GREEN经Session/Inventory/Repository同事务扣15600、升高级并达到40级阈值且不发044实物");
            string committedSnapshot = Snapshot(path);
            var duplicate = session.BuyCashShop(new(0, [new(105658, 0)]), cash, null,
                out var duplicateReceipt, out _, out var duplicateCommit);
            That(!duplicate.Accepted && duplicateReceipt is null && duplicateCommit is null &&
                Snapshot(path) == committedSnapshot && CurrencyWallet.Get(session.Selected!.Inventory, CurrencyKind.Cera) == afterCera,
                "豪华重复请求不再扣款或改写任一持久化事实");
            var reopened = new CharacterRepository(path);
            reopened.ConfigureBattlePassMissions(policy);
            var reopenedOwners = new AccountOwners();
            using var reopenedOwner = reopenedOwners.Acquire("certificate-deluxe", () => { });
            var reopenedSession = new GameSession(reopened, resources.Towns, reopenedOwner, timeProvider: clock);
            reopenedSession.Select(0); reopenedSession.MarkReady();
            var restored = reopened.ReadBattlePassMissions(reopenedOwner, now)!;
            That(restored.Season.PassType == state.Season.PassType && restored.Season.TotalExperience == state.Season.TotalExperience &&
                restored.Season.Revision == state.Season.Revision && restored.Season.Claims.SetEquals(state.Season.Claims) &&
                CurrencyWallet.Get(reopenedSession.Selected!.Inventory, CurrencyKind.Cera) == afterCera &&
                Number(path, "SELECT count(*) FROM account_battle_pass_deluxe_orders") == 1,
                "新Repository、新owner和Session恢复已提交豪华资格、经验、领取位、余额及唯一订单");
            foreach (long upgradeXp in new[] { threshold - 1, threshold, threshold + 10000 })
            {
                string upgradePath = Path.Combine(root, $"deluxe-upgrade-{upgradeXp}.db");
                var upgradeRepository = new CharacterRepository(upgradePath);
                upgradeRepository.ConfigureBattlePassMissions(policy);
                using var upgradeOwner = owners.Acquire($"certificate-deluxe-upgrade-{upgradeXp}", () => { });
                var upgradeCharacter = upgradeRepository.Create(upgradeOwner,
                    new NewCharacter("DeluxeUp", resources.Towns.Room, resources.Towns.InitialReturn) { Level = 95, AcceptedStoryLevel = 95 });
                InventoryService.Commit(upgradeRepository, upgradeOwner, upgradeCharacter,
                    new CurrencyService(items.GoldLimits).Grant(upgradeCharacter, CurrencyKind.Cera, 30000));
                var upgradeSession = new GameSession(upgradeRepository, resources.Towns, upgradeOwner, timeProvider: clock);
                upgradeSession.Select(0); upgradeSession.MarkReady(); _ = upgradeRepository.ReadBattlePassMissions(upgradeOwner, now);
                var ordinary = upgradeSession.BuyCashShop(new(0, [new(105659, 0)]), cash, null,
                    out _, out _, out _);
                Execute(upgradePath, $"UPDATE account_battle_pass_seasons SET total_experience={upgradeXp}");
                var beforeUpgrade = upgradeRepository.ReadBattlePassMissions(upgradeOwner, now)!;
                long ceraBeforeUpgrade = CurrencyWallet.Get(upgradeSession.Selected!.Inventory, CurrencyKind.Cera);
                var deluxeUpgrade = upgradeSession.BuyCashShop(new(0, [new(105658, 0)]), cash, null,
                    out var upgradeReceipt, out _, out var upgradeCommit);
                var afterUpgrade = upgradeRepository.ReadBattlePassMissions(upgradeOwner, now)!;
                long expectedUpgradeXp = Math.Max(checked(upgradeXp + 7800), threshold);
                That(ordinary.Accepted && beforeUpgrade.Season.PassType == 1 && beforeUpgrade.Season.TotalExperience == upgradeXp &&
                    deluxeUpgrade.Accepted && upgradeReceipt is not null && upgradeCommit is not null && afterUpgrade.Season.PassType == 1 &&
                    ceraBeforeUpgrade - CurrencyWallet.Get(upgradeSession.Selected!.Inventory, CurrencyKind.Cera) == 15600 &&
                    afterUpgrade.Season.TotalExperience == expectedUpgradeXp && afterUpgrade.Season.TotalExperience >= upgradeXp &&
                    Number(upgradePath, "SELECT count(*) FROM account_battle_pass_certificate_orders") == 1 &&
                    Number(upgradePath, "SELECT count(*) FROM account_battle_pass_deluxe_orders") == 1 &&
                    Number(upgradePath, "SELECT before_total_experience FROM account_battle_pass_deluxe_orders") == upgradeXp &&
                    Number(upgradePath, "SELECT paid_total_experience FROM account_battle_pass_deluxe_orders") == upgradeXp + 7800 &&
                    Number(upgradePath, "SELECT resulting_total_experience FROM account_battle_pass_deluxe_orders") == expectedUpgradeXp,
                    $"普通证已购且购买前经验{upgradeXp}时仍可购买豪华，按真实付款增加经验且不降低既有等级");
            }
            const int changedPrice = 17800;
            string changedPricePath = Path.Combine(root, "deluxe-changed-price.db");
            var changedPriceRepository = new CharacterRepository(changedPricePath);
            changedPriceRepository.ConfigureBattlePassMissions(policy);
            using var changedPriceOwner = owners.Acquire("certificate-deluxe-changed-price", () => { });
            var changedPriceCharacter = changedPriceRepository.Create(changedPriceOwner,
                new NewCharacter("DeluxePrice", resources.Towns.Room, resources.Towns.InitialReturn) { Level = 95, AcceptedStoryLevel = 95 });
            InventoryService.Commit(changedPriceRepository, changedPriceOwner, changedPriceCharacter,
                new CurrencyService(items.GoldLimits).Grant(changedPriceCharacter, CurrencyKind.Cera, 30000));
            var changedPriceSession = new GameSession(changedPriceRepository, resources.Towns, changedPriceOwner, timeProvider: clock);
            changedPriceSession.Select(0); changedPriceSession.MarkReady();
            _ = changedPriceRepository.ReadBattlePassMissions(changedPriceOwner, now);
            var changedPriceShop = CashShopCatalog.Parse($"[package]\n105658 490705044 1 0 {changedPrice} 0 0 0 0 0 0\n[/package]\n[buy only cera]\n490705044\n[/buy only cera]");
            var changedPriceCash = new InventoryCashShopService(items.Items, changedPriceShop, items.GoldLimits, items.EpicPieces, items.Cargo,
                contracts: items.Contracts, avatars: items.AvatarPackages, clock: clock, deluxeOffer: BattlePassDeluxePurchase.Resolve(changedPriceShop));
            var changedPriceResult = changedPriceSession.BuyCashShop(new(0, [new(105658, 0)]), changedPriceCash, null,
                out var changedPriceReceipt, out _, out var changedPriceCommit);
            var changedPriceState = changedPriceRepository.ReadBattlePassMissions(changedPriceOwner, now)!;
            That(changedPriceResult.Accepted && changedPriceReceipt is not null && changedPriceCommit is not null &&
                CurrencyWallet.Get(changedPriceSession.Selected!.Inventory, CurrencyKind.Cera) == 30000 - changedPrice &&
                changedPriceState.Season.PassType == 1 && changedPriceState.Season.TotalExperience == Math.Max(8900, threshold) &&
                Number(changedPricePath, "SELECT count(*) FROM account_battle_pass_deluxe_orders WHERE commodity_id=105658 AND item_id=490705044 AND cera=17800 AND before_total_experience=0 AND paid_total_experience=8900") == 1 &&
                Number(changedPricePath, "SELECT resulting_total_experience FROM account_battle_pass_deluxe_orders") == Math.Max(8900, threshold) &&
                Number(changedPricePath, "SELECT count(*) FROM account_battle_pass_cera_orders WHERE cera=17800 AND eligible_cera=17800 AND experience=8900 AND percentage=50 AND before_remainder=0 AND resulting_remainder=0") == 1 &&
                Number(changedPricePath, "SELECT total_cera FROM account_battle_pass_cera_consumption") == changedPrice &&
                Number(changedPricePath, "SELECT numerator_remainder FROM account_battle_pass_cera_consumption") == 0 &&
                changedPriceSession.Selected.Inventory.Items.All(row => row.Core.ItemId != BattlePassDeluxePurchase.Item),
                "豪华资源改价17800经真实购买事务精确扣款，消费订单记8900经验且豪华订单保留原价及40级抬升审计");
            string failurePath = Path.Combine(root, "deluxe-failure.db");
            var failureRepository = new CharacterRepository(failurePath);
            failureRepository.ConfigureBattlePassMissions(policy);
            using var failureOwner = owners.Acquire("certificate-deluxe-failure", () => { });
            var failureCharacter = failureRepository.Create(failureOwner,
                new NewCharacter("DeluxeFail", resources.Towns.Room, resources.Towns.InitialReturn) { Level = 95, AcceptedStoryLevel = 95 });
            InventoryService.Commit(failureRepository, failureOwner, failureCharacter,
                new CurrencyService(items.GoldLimits).Grant(failureCharacter, CurrencyKind.Cera, 30000));
            var failureSession = new GameSession(failureRepository, resources.Towns, failureOwner, timeProvider: clock);
            failureSession.Select(0); failureSession.MarkReady(); _ = failureRepository.ReadBattlePassMissions(failureOwner, now);
            Execute(failurePath, "CREATE TRIGGER fail_deluxe_order BEFORE INSERT ON account_battle_pass_deluxe_orders BEGIN SELECT RAISE(ABORT,'deluxe late failure'); END");
            string failureBefore = Snapshot(failurePath);
            long failureCera = CurrencyWallet.Get(failureSession.Selected!.Inventory, CurrencyKind.Cera);
            bool failed = false;
            try
            {
                try { _ = failureSession.BuyCashShop(new(0, [new(105658, 0)]), cash, null, out _, out _, out _); }
                catch (SqliteException) { failed = true; }
                That(failed && Snapshot(failurePath) == failureBefore &&
                    CurrencyWallet.Get(failureSession.Selected!.Inventory, CurrencyKind.Cera) == failureCera,
                    "豪华订单晚插入失败回滚库存、钱包、消费经验、资格、经验及所有订单事实");
            }
            finally { Execute(failurePath, "DROP TRIGGER fail_deluxe_order"); }
            var retried = failureSession.BuyCashShop(new(0, [new(105658, 0)]), cash, null, out _, out _, out var retriedCommit);
            That(retried.Accepted && retriedCommit is not null &&
                Number(failurePath, "SELECT count(*) FROM account_battle_pass_deluxe_orders") == 1,
                "移除晚故障后原合法请求可成功一次");
            string highPath = Path.Combine(root, "deluxe-high.db");
            var highRepository = new CharacterRepository(highPath);
            highRepository.ConfigureBattlePassMissions(policy);
            using var highOwner = owners.Acquire("certificate-deluxe-high", () => { });
            var highCharacter = highRepository.Create(highOwner,
                new NewCharacter("DeluxeHigh", resources.Towns.Room, resources.Towns.InitialReturn) { Level = 95, AcceptedStoryLevel = 95 });
            InventoryService.Commit(highRepository, highOwner, highCharacter,
                new CurrencyService(items.GoldLimits).Grant(highCharacter, CurrencyKind.Cera, 30000));
            _ = highRepository.ReadBattlePassMissions(highOwner, now);
            long highBefore = checked(threshold + 10000);
            Execute(highPath, $"UPDATE account_battle_pass_seasons SET total_experience={highBefore} WHERE account='certificate-deluxe-high'");
            var highSession = new GameSession(highRepository, resources.Towns, highOwner, timeProvider: clock);
            highSession.Select(0); highSession.MarkReady();
            var highPurchase = highSession.BuyCashShop(new(0, [new(105658, 0)]), cash, null, out _, out _, out var highCommit);
            var highAfter = highRepository.ReadBattlePassMissions(highOwner, now)!;
            That(highPurchase.Accepted && highCommit is not null && highAfter.Season.PassType == 1 &&
                highAfter.Season.TotalExperience >= highBefore && Number(highPath, "SELECT count(*) FROM account_battle_pass_certificate_orders") == 0,
                "未购买普通证的type0高等级账号可购买豪华，已有累计经验不降低");
            void Execute(string database, string sql)
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
                connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
            string Snapshot(string database)
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
                connection.Open(); using var tx = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = tx;
                command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' ORDER BY name";
                var tables = new List<string>();
                using (var reader = command.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));
                var snapshot = new List<object>();
                foreach (string table in tables)
                {
                    command.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"") + "\" ORDER BY rowid";
                    var rows = new List<object?[]>(); using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var row = new object?[reader.FieldCount];
                        for (int i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        rows.Add(row);
                    }
                    snapshot.Add(new { table, rows });
                }
                tx.Commit(); return System.Text.Json.JsonSerializer.Serialize(snapshot);
            }
            long Number(string database, string sql)
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
                connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            }
            await Task.CompletedTask;
        }

        async Task Scenario(string change, bool deluxe)
        {
            const string season = "certificate-publication-race";
            var items = resources.ItemResources;
            int commodity = deluxe ? 105658 : 105659;
            int price = deluxe ? 15600 : 4900;
            int startingCera = deluxe ? 25000 : 10000;
            int paidBalance = startingCera - price;
            string orders = deluxe ? "account_battle_pass_deluxe_orders" : "account_battle_pass_certificate_orders";
            string kind = deluxe ? "deluxe" : "ordinary";
            // 合成商品只用于豪华发布交错测试；普通证继续使用真实目录。
            var shop = deluxe ? CashShopCatalog.Parse("[package]\n105658 490705044 1 0 15600 0 0 0 0 0 0\n[/package]\n[buy only cera]\n490705044\n[/buy only cera]") : items.CashShop;
            var product = shop.Product(commodity);
            That(product is { Count: 1, Section: "package" } && product.ItemId == (deluxe ? 490705044 : 490705043) &&
                product.CeraPrice == price && shop.Mileage.CeraOnly.Contains(product.ItemId) && shop.Bonus(commodity).Count == 0,
                "资格购买发布夹具是单件真实点券且无附赠：" + kind);
            var (policy, now) = BattlePassMissionTests.AckFixturePolicy(season);
            var clock = new Clock(now);
            string path = Path.Combine(root, kind + "-" + change + ".db");
            var repository = new CharacterRepository(path);
            repository.ConfigureBattlePassMissions(policy);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("certificate-race", () => { });
            repository.Create(owner, new NewCharacter("CertOne", resources.Towns.Room, resources.Towns.InitialReturn) { Level = 95 });
            repository.Create(owner, new NewCharacter("CertTwo", resources.Towns.Room, resources.Towns.InitialReturn) { Level = 95 });
            var session = new GameSession(repository, resources.Towns, owner, timeProvider: clock);
            session.Select(0); session.MarkReady();
            That(DevelopmentGm.Answer("recharge cera " + startingCera, owner.Account, repository, owners, _ => session)
                .StartsWith("OK ", StringComparison.Ordinal), "隔离账号准备足额真实点券");
            var initial = session.Selected!;
            long epoch = session.SelectionEpoch;
            var computer = new CharacterStatComputer(resources.Characters);
            var inventory = new InventoryProjection(computer,
                new TownProjection(resources.Towns, computer, null, items.EpicPieces), items.EpicPieces);
            var cash = new InventoryCashShopService(items.Items, shop, items.GoldLimits, items.EpicPieces, items.Cargo,
                contracts: items.Contracts, avatars: items.AvatarPackages, clock: clock, deluxeOffer: BattlePassDeluxePurchase.Resolve(shop));
            var battlePass = new BattlePassProjection(season, policy.Catalog, clock);
            using var log = new ServerLog(Path.Combine(root, "log-" + kind + "-" + change));
            var commands = new GameCommandRegistry();
            new ShopHandler(new(items.Items, items.Shops, items.GoldLimits, items.EpicPieces), cash, inventory, log,
                battlePassCertificateChanged: battlePass.MissionStateAsync).Register(commands);
            commands.Seal();
            using var stop = new CancellationTokenSource();
            using var output = new HeldOutput(change == "recharge-twice");
            var sent = new List<(byte Kind, ushort Opcode, byte[] Body, string Action)>();
            await using var sender = new GameConnectionSender(owner, output, stop, 0,
                (kind, opcode, _, body, action) => sent.Add((kind, opcode, body.ToArray(), action)));
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, output, stop), owner, session, sender);
            Task held = sender.Send(1, 0, [], "held");
            await output.Started.WaitAsync(TimeSpan.FromSeconds(5));
            long heldBytes = sender.PendingBytes;
            byte[] body = new byte[19]; body[2] = 1; body[5] = 0xFF;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), checked((uint)commodity));
            Task<CommandResult> purchase = commands.DispatchAsync(context, new(1, (ushort)CmdPacketType.BUY_CERASHOP_ITEM, body));
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (sender.PendingBytes <= heldBytes && !purchase.IsCompleted && wait.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Yield();
            var purchased = session.Selected!;
            var state = repository.ReadBattlePassMissions(owner, now)!;
            That(sender.PendingBytes > heldBytes && !purchase.IsCompleted &&
                !ReferenceEquals(initial, purchased) && purchased.Id == initial.Id && session.SelectionEpoch == epoch &&
                purchased.Inventory.AccountWallet.Get(CurrencyKind.Cera) == paidBalance && state.Season.PassType == 1 &&
                Number("SELECT count(*) FROM " + orders) == 1,
                "资格事务已提交实际扣款和唯一订单，成功ACK排队待发送：" + kind);
            if (deluxe)
                That(state.Season.TotalExperience >= checked((long)policy.Catalog.TotalExperienceToReach(40)),
                    "豪华发布前已提交当前资源40级阈值");
            string order = Text("SELECT order_id FROM " + orders);
            AccountOwners.Lease? replacement = null;
            try
            {
                if (change is "recharge" or "recharge-twice")
                {
                    string answer = DevelopmentGm.Answer("recharge cera 100", owner.Account, repository, owners, _ => session);
                    That(answer.StartsWith("OK ", StringComparison.Ordinal) && !ReferenceEquals(session.Selected, purchased) &&
                        session.Selected!.Id == purchased.Id && session.SelectionEpoch == epoch &&
                        session.Selected.Inventory.AccountWallet.Get(CurrencyKind.Cera) == paidBalance + 100,
                        "在线充值提交后保持相同角色和选角纪元");
                }
                else if (change == "reselect")
                {
                    session.ReturnToRoster(); session.Select(1); session.MarkReady();
                    That(session.SelectionEpoch != epoch && session.Selected!.Id != initial.Id, "换角阻断旧购买结果投影");
                }
                else if (change == "takeover")
                {
                    replacement = owners.Acquire(owner.Account, () => { });
                    That(!owner.IsCurrent && replacement.IsCurrent, "旧购买owner已被接管");
                }
            }
            finally { output.Release(); }
            if (change == "recharge-twice")
            {
                try
                {
                    await output.CeraStarted.WaitAsync(TimeSpan.FromSeconds(5));
                    That(sent.Count(row => row.Kind == 1 && row.Opcode == (ushort)CmdPacketType.BUY_CERASHOP_ITEM &&
                            row.Body.Length == 24 && row.Body[0] == 1) == 1 &&
                        session.Selected!.Inventory.AccountWallet.Get(CurrencyKind.Cera) == paidBalance + 100 && !purchase.IsCompleted,
                        "原成功ACK已写出一次，恢复组的首次真实点券帧正在发送");
                    foreach (int expected in new[] { paidBalance + 200, paidBalance + 300 })
                    {
                        var previous = session.Selected!;
                        string answer = DevelopmentGm.Answer("recharge cera 100", owner.Account, repository, owners, _ => session);
                        That(answer.StartsWith("OK ", StringComparison.Ordinal) && session.Selected is { } next &&
                            !ReferenceEquals(next, previous) && next.Id == initial.Id && session.SelectionEpoch == epoch &&
                            next.Inventory.AccountWallet.Get(CurrencyKind.Cera) == expected &&
                            repository.LoadCharacter(owner, 0).Inventory.AccountWallet.Get(CurrencyKind.Cera) == expected,
                            "恢复组发送期间连续真实充值并更新同角色快照：" + expected);
                    }
                }
                finally { output.ReleaseCera(); }
            }
            try
            {
                await Observe(held);
                await Observe(purchase);
            }
            finally { replacement?.Dispose(); }

            async Task Observe(Task task)
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception error) when (change == "takeover" &&
                    error is InvalidOperationException or IOException or OperationCanceledException) { }
            }

            That(Number("SELECT count(*) FROM " + orders) == 1 &&
                Text("SELECT order_id FROM " + orders) == order &&
                Number("SELECT count(*) FROM currency_audit_log WHERE action_name='cash_shop_buy' AND currency_kind=15") == 1 &&
                Number("SELECT pass_type FROM account_battle_pass_seasons") == 1,
                "发送交错后保留原唯一订单和一次实际扣款，不重建订单");
            var acks = sent.Where(row => row.Kind == 1 && row.Opcode == (ushort)CmdPacketType.BUY_CERASHOP_ITEM).ToArray();
            if (change is "unchanged" or "recharge" or "recharge-twice")
            {
                int expectedBalance = paidBalance + (change == "recharge-twice" ? 300 : change == "recharge" ? 100 : 0);
                That(acks.Length == 1 && acks[0].Body.Length == 24 && acks[0].Body[0] == 1 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(acks[0].Body.AsSpan(6)) == commodity,
                    "同角色同纪元收到原一次证书购买成功ACK：" + change);
                var cera = sent.Where(row => row.Kind == 0 && row.Opcode == (ushort)NotiPacketType.CERA).ToArray();
                That(cera.Length > 0 && cera[^1].Body.Length == 13 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(cera[^1].Body.AsSpan(1)) == expectedBalance,
                    "最后的点券投影反映同角色当前实际余额：" + change);
                if (change == "recharge-twice")
                    That(cera.Length >= 2 && BinaryPrimitives.ReadUInt32LittleEndian(cera[0].Body.AsSpan(1)) == paidBalance + 100 &&
                        Number("SELECT count(*) FROM currency_audit_log WHERE action_name='gm_recharge' AND currency_kind=15") == 4 &&
                        sent.All(row => row.Action != "inventory_committed_delta" && row.Action != "mileage_committed_delta"),
                        "在途旧余额后重读最新余额；四次真实充值均持久化且没有重放购买获得型增量");
                var pass = sent.Where(row => row.Kind == 0 && row.Opcode is
                    (ushort)NotiPacketType.BATTLE_PASS_INFO_PASS or (ushort)NotiPacketType.BATTLE_PASS_INFO_MISSION or
                    (ushort)NotiPacketType.BATTLE_PASS_INFO_REWARD).ToArray();
                That(pass.Length >= 3 && pass.TakeLast(3).Select(row => row.Opcode).SequenceEqual(new ushort[]
                    { (ushort)NotiPacketType.BATTLE_PASS_INFO_PASS, (ushort)NotiPacketType.BATTLE_PASS_INFO_MISSION,
                      (ushort)NotiPacketType.BATTLE_PASS_INFO_REWARD }) &&
                    pass[^3].Body.SequenceEqual(BattlePassProjection.PassInfo(policy.Catalog, state.Season)) &&
                    pass[^2].Body.SequenceEqual(state.ToPayload()) && pass[^1].Body.SequenceEqual(state.Season.ToClaimBytes()),
                    "购买成功后送达完整且一致的资格、任务和领奖状态：" + change);
                var reopened = new CharacterRepository(path);
                reopened.ConfigureBattlePassMissions(policy);
                That(reopened.LoadCharacter(owner, 0).Inventory.AccountWallet.Get(CurrencyKind.Cera) == expectedBalance &&
                    reopened.ReadBattlePassMissions(owner, now)!.Season.PassType == 1,
                    "重开仓储恢复真实余额和购买资格");
            }
            else
                That(acks.Length == 0 && sent.All(row => row.Opcode != (ushort)NotiPacketType.BATTLE_PASS_INFO_PASS &&
                    row.Opcode != (ushort)NotiPacketType.BATTLE_PASS_INFO_MISSION && row.Opcode != (ushort)NotiPacketType.BATTLE_PASS_INFO_REWARD),
                    "换角或接管后不向新身份发送旧购买结果");

            long Number(string sql) => Convert.ToInt64(Scalar(sql));
            string Text(string sql) => (string)Scalar(sql)!;
            object? Scalar(string sql)
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
                db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar();
            }
        }
    }

    sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    sealed class HeldOutput(bool holdCera) : MemoryStream
    {
        readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource ceraStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource ceraRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int ceraHeld;
        public Task Started => started.Task;
        public Task CeraStarted => ceraStarted.Task;
        public void Release() => release.TrySetResult();
        public void ReleaseCera() => ceraRelease.TrySetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            if (holdCera && IsCera(buffer) && Interlocked.Exchange(ref ceraHeld, 1) == 0)
            {
                ceraStarted.TrySetResult();
                await ceraRelease.Task.WaitAsync(cancellationToken);
            }
            await base.WriteAsync(buffer, cancellationToken);
        }

        static bool IsCera(ReadOnlyMemory<byte> frame) => frame.Length >= 16 && frame.Span[0] == 0 &&
            BinaryPrimitives.ReadUInt16LittleEndian(frame.Span[1..]) == (ushort)NotiPacketType.CERA;
    }
}
