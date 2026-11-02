using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static partial class CashShopTests
{
    static void VerifyHongbaoStorage(TownCatalog towns, CharacterCatalog characters, ItemResources resources)
    {
        string root = Path.Combine(Path.GetTempPath(), "guild-hongbao-purchase-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("hongbao-buyer", static () => { });
            var buyer = repo.Create(owner, new("HongbaoBuyer", towns.Room, towns.InitialReturn)
            {
                Skills = characters.Get(0).Skills,
                Inventory = InventoryRewardGrantService.CreateStarter(characters.Get(0))
            });
            buyer = InventoryService.Commit(repo, owner, buyer,
                new CurrencyService(resources.GoldLimits).Grant(buyer, CurrencyKind.Cera, 200_000));
            var catalog = resources.GuildHongbao;
            var product = catalog.ByCommodity.Values.OrderBy(row => row.Cera).First();
            var service = new InventoryCashShopService(resources.Items, resources.CashShop, resources.GoldLimits,
                resources.EpicPieces, resources.Cargo, contracts: resources.Contracts, hongbao: catalog) { IgnoreAbsoluteExpiration = true };
            InventoryResult Buy() => service.Buy(buyer, new(0, [new(product.CommodityId, 0)]), out _);
            SqliteConnection Open() { var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open(); return db; }
            void Sql(string sql) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
            long Number(string sql) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
            long Cera() => repo.LoadCharacter(owner, buyer.Slot)!.Inventory.AccountWallet.Get(CurrencyKind.Cera);
            That(!Buy().Accepted, "无会籍不能规划红包购买，不扣真实点券");
            Sql($"""
                INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix) VALUES(1,'红包购买','hongbao-buy',{buyer.Id},1000);
                INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{buyer.Id},1,1000);
                """);
            ushort AddMember(int index)
            {
                using var member = owners.Acquire("hongbao-member-" + index, static () => { });
                ushort id = repo.Create(member, new("Hongbao" + index, towns.Room, towns.InitialReturn)).Id;
                Sql($"INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{id},4,1000)");
                return id;
            }
            for (int index = 1; index < catalog.MinimumMembers - 1; index++) AddMember(index);
            buyer = repo.LoadCharacter(owner, buyer.Slot)!;
            var tooFew = Buy();
            Reject<GuildHongbaoPurchaseRejectedException>(() => repo.CommitInventory(owner, buyer, tooFew.Plan!),
                "真实人数比门槛少一人在最终购买事务拒绝");
            That(Cera() == 200_000 && Number("SELECT count(*) FROM character_guild_hongbao_purchases") == 0,
                "成员不足不扣款、不写额度、不发红包");
            AddMember(catalog.MinimumMembers - 1);
            buyer = repo.LoadCharacter(owner, buyer.Slot)!;
            var purchase = Buy();
            var events = new DfoServer.Game.Events.ServerEventService(new ServerEventRepository(path));
            events.Override(2378, false);
            Reject<GuildHongbaoPurchaseRejectedException>(() => repo.CommitInventory(owner, buyer, purchase.Plan!),
                "购买规划完成后关闭红包，最终SQL事务仍拒绝");
            That(Cera() == 200_000 && Number("SELECT count(*) FROM character_guild_hongbao_purchases") == 0,
                "关闭拒绝不扣款、不发道具、不增加额度");
            events.Override(2378, true);
            That(purchase.Accepted && purchase.Plan!.WithAfter(purchase.Plan.After).GuildHongbaoPurchase == purchase.Plan.GuildHongbaoPurchase,
                "合并库存计划保留红包最终事务约束");
            Sql("CREATE TRIGGER hongbao_late_failure BEFORE INSERT ON currency_audit_log WHEN NEW.action_name='cash_shop_buy' BEGIN SELECT RAISE(ABORT,'late failure'); END");
            Reject<SqliteException>(() => repo.CommitInventory(owner, buyer, purchase.Plan!), "红包扣款后审计末端真实SQL故障");
            That(Cera() == 200_000 && Number("SELECT count(*) FROM character_guild_hongbao_purchases") == 0 &&
                repo.LoadCharacter(owner, buyer.Slot)!.Revision == buyer.Revision,
                "末端故障把购买额度、点券、物品与角色版本一起回滚");
            Sql("DROP TRIGGER hongbao_late_failure");
            var stale = buyer;
            buyer = repo.CommitInventory(owner, buyer, purchase.Plan!);
            That(Cera() == 200_000 - product.Cera && Number("SELECT cera FROM character_guild_hongbao_purchases") == product.Cera &&
                buyer.Inventory.Items.Any(item => item.Core.ItemId == product.ItemId && item.Uid > 0) &&
                repo.ReadGuildPanel(owner, buyer.Id)!.HongbaoSpentCera == product.Cera,
                "达到人数门槛后同事务扣真实点券、分配真实UID并累计角色额度");
            Reject<InvalidOperationException>(() => repo.CommitInventory(owner, stale, purchase.Plan!), "旧购买计划重复提交不重复扣款或计额度");
            Sql($"UPDATE character_guild_hongbao_purchases SET cera={catalog.DailyCeraLimit - product.Cera}");
            buyer = repo.CommitInventory(owner, buyer, Buy().Plan!);
            That(Number("SELECT cera FROM character_guild_hongbao_purchases") == catalog.DailyCeraLimit,
                "恰好达到每日点券上限允许最后一笔完整购买");
            long paid = Cera();
            Reject<GuildHongbaoPurchaseRejectedException>(() => repo.CommitInventory(owner, buyer, Buy().Plan!), "超过角色日额度拒绝整笔购买");
            Sql($"""
                INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix) VALUES(2,'换会购买','hongbao-next',{buyer.Id},1001);
                UPDATE guild_members SET guild_id=2,joined_at_unix=1001;
                DELETE FROM guilds WHERE guild_id=1;
                """);
            buyer = repo.LoadCharacter(owner, buyer.Slot)!;
            Reject<GuildHongbaoPurchaseRejectedException>(() => repo.CommitInventory(owner, buyer, Buy().Plan!),
                "换会并解散原公会不能清空角色当日购买额度");
            That(Cera() == paid && Number("SELECT cera FROM character_guild_hongbao_purchases") == catalog.DailyCeraLimit,
                "额度拒绝保持完整资产和当日累计");
            Sql("UPDATE character_guild_hongbao_purchases SET day_start_unix=day_start_unix-86400");
            buyer = repo.CommitInventory(owner, buyer, Buy().Plan!);
            repo = new CharacterRepository(path);
            That(Number("SELECT count(*) FROM character_guild_hongbao_purchases") == 2 &&
                Number("SELECT cera FROM character_guild_hongbao_purchases ORDER BY day_start_unix DESC LIMIT 1") == product.Cera &&
                Cera() == paid - product.Cera, "新公会日单独累计并在重开仓储后恢复，旧日事实保留");
            var lostMembership = Buy();
            Sql($"DELETE FROM guild_members WHERE character_id={buyer.Id}");
            Reject<GuildHongbaoPurchaseRejectedException>(() => repo.CommitInventory(owner, buyer, lostMembership.Plan!),
                "规划后被踢不能凭旧会籍提交红包购买");
            repo.Delete(owner, repo.LoadDeletionTarget(owner, buyer.Slot));
            That(Number("SELECT count(*) FROM character_guild_hongbao_purchases") == 0,
                "删角事务级联清理个人红包购买记录，不遗留外键依赖");
        }
        finally { TestFiles.DeleteDirectory(root, true); }
    }
}
