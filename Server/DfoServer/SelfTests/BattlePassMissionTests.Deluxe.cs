using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class BattlePassMissionTests
{
    static void DeluxePurchasePersistence()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pass-deluxe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool succeeded = false;
        try
        {
            string path = Path.Combine(root, "game.db");
            var now = Start.AddDays(10).AddHours(1);
            var clock = new MissionClock(now);
            var policy = new BattlePassMissionPolicy("deluxe-season", Start, Catalog());
            var repository = new CharacterRepository(path);
            repository.ConfigureBattlePassMissions(policy, clock);
            new AuthenticationService(new AuthenticationRepository(path)).Create("deluxe-pass", "fixture-password");
            new AuthenticationService(new AuthenticationRepository(path)).Create("deluxe-rich", "fixture-password");
            var owners = new AccountOwners();
            using var owner = owners.Acquire("deluxe-pass", () => { });
            var role = repository.Create(owner, new NewCharacter("DeluxePass", new Location(1, 0, 10, 10), null) { GrowType = 33, Level = 95 });
            role = repository.CommitInventory(owner, role, new InventoryPlan(role.Inventory,
                CurrencyWallet.Set(role.Inventory, CurrencyKind.Cera, 60_000), "fixture_credit", new(role)));
            long floor = checked((long)policy.Catalog.TotalExperienceToReach(BattlePassDeluxePurchase.TargetLevel));
            That(floor == 50_700, "合成目录40级阈值与当前战令曲线一致");
            var offer = new CashShopProduct(BattlePassDeluxePurchase.Commodity, BattlePassDeluxePurchase.Item, 1, 15_600, "package");
            var bought = CommitDeluxe(repository, owner, role, policy, offer, 60_000, now, clock);
            That(bought.State.Season is { PassType: 1, TotalExperience: 50_700 } &&
                bought.State.Season.Claims.IsEmpty &&
                CurrencyWallet.Get(bought.Character.Inventory, CurrencyKind.Cera) == 44_400 &&
                !bought.Character.Inventory.Items.Any(row => row.Core.ItemId == BattlePassDeluxePurchase.Item),
                "零经验购买豪华只扣15600真实点券，资格升到type1并把累计经验抬到50700，不发放044");
            using (var db = new SqliteGameDatabase(path).Open())
            {
                using (var query = db.CreateCommand())
                {
                    query.CommandText = "SELECT cera,resulting_total_experience FROM account_battle_pass_deluxe_orders";
                    using var reader = query.ExecuteReader();
                    That(reader.Read() && reader.GetInt64(0) == 15_600 && reader.GetInt64(1) == 50_700 && !reader.Read(),
                        "豪华订单记录实际报价和抬升后的累计经验");
                }
                using var experience = db.CreateCommand();
                experience.CommandText = "SELECT experience FROM account_battle_pass_cera_orders";
                That((long)experience.ExecuteScalar()! == 7_800, "15600真实点券仍先按50%记7800消费经验，再与40级阈值取较大值");
            }
            Reject<BattlePassCertificateChangedException>(() => CommitDeluxe(repository, owner, bought.Character, policy, offer, 44_400, now, clock),
                "同一账号再次购买豪华整笔拒绝");
            That(CurrencyWallet.Get(repository.LoadCharacter(owner, role.Slot).Inventory, CurrencyKind.Cera) == 44_400,
                "重复豪华购买不二次扣点券");
            var ordinary = CertificatePlan(bought.Character, bought.State.Season, 44_400, 39_500, now, clock);
            Reject<BattlePassCertificateChangedException>(() => repository.CommitBattlePassCertificate(owner, bought.Character, ordinary, now),
                "豪华之后不能再买普通资格证");

            using var richOwner = owners.Acquire("deluxe-rich", () => { });
            var rich = repository.Create(richOwner, new NewCharacter("DeluxeRich", new Location(1, 0, 10, 10), null) { GrowType = 33, Level = 95 });
            rich = repository.CommitInventory(richOwner, rich, new InventoryPlan(rich.Inventory,
                CurrencyWallet.Set(CurrencyWallet.Set(rich.Inventory, CurrencyKind.Cera, 80_000), CurrencyKind.TokenCera, 9_000),
                "fixture_credit", new(rich)));
            _ = repository.ReadBattlePassMissions(richOwner, now);
            using (var db = new SqliteGameDatabase(path).Open())
            using (var update = db.CreateCommand())
            {
                update.CommandText = "UPDATE account_battle_pass_seasons SET total_experience=60000 WHERE account=$account AND season_key=$season";
                update.Parameters.AddWithValue("$account", richOwner.Account);
                update.Parameters.AddWithValue("$season", policy.SeasonKey);
                That(update.ExecuteNonQuery() == 1, "高经验夹具只改当前账号季累计经验");
            }
            var kept = CommitDeluxe(repository, richOwner, rich, policy, offer, 80_000, now, clock);
            That(kept.State.Season.TotalExperience == 67_800 && kept.State.Season.PassType == 1 &&
                CurrencyWallet.Get(kept.Character.Inventory, CurrencyKind.Cera) == 64_400 &&
                CurrencyWallet.Get(kept.Character.Inventory, CurrencyKind.TokenCera) == 9_000,
                "已有60000经验时保留消费后的67800，代币券不参与豪华扣款");
            succeeded = true;
        }
        finally
        {
            if (succeeded) TestFiles.DeleteDirectory(root, recursive: true);
            else Console.Error.WriteLine($"Battle-pass deluxe fixture preserved: {root}");
        }

        static BattlePassCertificateCommit CommitDeluxe(CharacterRepository repository, AccountOwners.Lease owner, Character role,
            BattlePassMissionPolicy policy, CashShopProduct offer, int beforeCera, DateTimeOffset now, MissionClock clock)
        {
            var state = repository.ReadBattlePassMissions(owner, now)!;
            var after = CurrencyWallet.Set(role.Inventory, CurrencyKind.Cera, beforeCera - offer.CeraPrice);
            var fact = CashShopPaymentFact.Capture(role.Inventory, role.Inventory, after, after,
                [new(offer, true)], offer.CeraPrice, 0, 0, now, clock);
            var plan = new BattlePassCertificatePlan(state.Season,
                new InventoryPlan(role.Inventory, after, "cash_shop_buy", new(role)) { CashShopPayment = fact },
                new CashShopPurchaseRequest(0, [new(offer.CommodityId, 0)]),
                new CashShopPurchaseReceipt([offer], new Dictionary<int, IReadOnlyList<CashShopBonus>>()),
                Guid.NewGuid(), now, clock, offer);
            return repository.CommitBattlePassCertificate(owner, role, plan, now);
        }

        static BattlePassCertificatePlan CertificatePlan(Character role, BattlePassSeasonState season, int beforeCera, int afterCera,
            DateTimeOffset now, MissionClock clock)
        {
            var product = new CashShopProduct(BattlePassCertificatePurchase.Commodity, BattlePassCertificatePurchase.Item, 1,
                BattlePassCertificatePurchase.Cera, "package");
            var after = CurrencyWallet.Set(role.Inventory, CurrencyKind.Cera, afterCera);
            var fact = CashShopPaymentFact.Capture(role.Inventory, role.Inventory, after, after,
                [new(product, true)], BattlePassCertificatePurchase.Cera, 0, 0, now, clock);
            return new(season, new InventoryPlan(role.Inventory, after, "cash_shop_buy", new(role)) { CashShopPayment = fact },
                new CashShopPurchaseRequest(0, [new(product.CommodityId, 0)]),
                new CashShopPurchaseReceipt([product], new Dictionary<int, IReadOnlyList<CashShopBonus>>()),
                Guid.NewGuid(), now, clock);
        }
    }
}
