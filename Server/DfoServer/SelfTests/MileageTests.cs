using System.Collections.Immutable;
using DfoServer.Game.Currency;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class MileageTests
{
    public static void RunCore()
    {
        var wallet = AccountWallet.Empty.Set(CurrencyKind.Mileage,100);
        That(CurrencyWallet.IsAccount(CurrencyKind.Mileage) && wallet.Mileage==100 && wallet.Get(CurrencyKind.Mileage)==100 &&
            wallet.Get(CurrencyKind.Cera)==0 && wallet.RentalPoints==0 && wallet.Balances.IsEmpty && !wallet.Same(AccountWallet.Empty),
            "商城积分独立存入账号钱包，不冒充点券、欢乐代币或租赁点，不分配实体物品");
        Reject<InvalidDataException>(() => wallet.Set(CurrencyKind.Mileage,-1),"积分不能为负数");
        Reject<InvalidDataException>(() => (wallet with { Balances=ImmutableSortedDictionary<CurrencyKind,int>.Empty.Add(CurrencyKind.Mileage,1) }).Validate(),
            "积分不能同时保存在独立列和其他账号货币行形成双真源");
        Bytes(MileagePackets.State(wallet),[0,100,0,0,0,0,0,0,0],"427初始化9B：实际余额与未接入的充值侧计数分开");
        Bytes(MileagePackets.Added(wallet,wallet.Set(CurrencyKind.Mileage,500)),[0,100,0,0,0,244,1,0,0,0,0,0,0],
            "537更新13B：先前余额、提交后余额，不能只发送增量覆盖绝对余额");

        string root=Path.Combine(Path.GetTempPath(),"S5A9-mileage-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path=Path.Combine(root,"game.db");
        try
        {
            using (var db=new SqliteGameDatabase(path).Open())
            {
                Sql(db,"INSERT INTO account_wallets(account,revision,rental_points,mileage) VALUES('keep',9,17,123)");
                Sql(db,"INSERT INTO account_currency_balances(account,currency_kind,balance) VALUES('keep',15,88)");
                That(Scalar(db,"SELECT mileage FROM account_wallets WHERE account='keep'") is 123L,"新建库含正式积分列");
                Sql(db,"ALTER TABLE account_wallets DROP COLUMN mileage; ALTER TABLE characters DROP COLUMN selected_skill_tree; PRAGMA user_version=74");
            }
            using (var db=new SqliteGameDatabase(path).Open())
            {
                That(Scalar(db,"PRAGMA user_version") is long version && version==SqliteMigrations.CurrentVersion &&
                    Scalar(db,"SELECT mileage FROM account_wallets WHERE account='keep'") is 0L &&
                    Scalar(db,"SELECT revision*100+rental_points FROM account_wallets WHERE account='keep'") is 917L &&
                    Scalar(db,"SELECT balance FROM account_currency_balances WHERE account='keep' AND currency_kind=15") is 88L,
                    "真实v74升级只补积分零值，不重建旧钱包、改revision或补发历史积分");
                Sql(db,"UPDATE account_wallets SET mileage=123 WHERE account='keep'");
                Reject<SqliteException>(() => Sql(db,"UPDATE account_wallets SET mileage=-1 WHERE account='keep'"),"数据库限制负积分");
                Sql(db,"ALTER TABLE account_wallets DROP COLUMN mileage; ALTER TABLE account_currency_balances RENAME COLUMN currency_kind TO broken_kind; ALTER TABLE characters DROP COLUMN selected_skill_tree; PRAGMA user_version=74");
            }
            bool failed=false;
            try { using var _=new SqliteGameDatabase(path).Open(); }
            catch (Exception error) when (error is SqliteException or InvalidDataException) { failed=true; }
            using (var db=Raw())
            {
                That(failed && Scalar(db,"PRAGMA user_version") is 74L &&
                    Scalar(db,"SELECT count(*) FROM pragma_table_info('account_wallets') WHERE name='mileage'") is 0L &&
                    Scalar(db,"SELECT balance FROM account_currency_balances WHERE account='keep'") is 88L,
                    "增列之后最终结构检查失败时，DDL、版本和旧余额整体回滚");
                Sql(db,"ALTER TABLE account_currency_balances RENAME COLUMN broken_kind TO currency_kind");
            }
            using (var db=new SqliteGameDatabase(path).Open()) Sql(db,"UPDATE account_wallets SET mileage=456 WHERE account='keep'");
            using (var db=new SqliteGameDatabase(path).Open())
                That(Scalar(db,"SELECT mileage FROM account_wallets WHERE account='keep'") is 456L,"重开库保留积分，不重复续增或重置");
        }
        finally { Directory.Delete(root,true); }
        Console.WriteLine("PASS 商城积分钱包、原生通知及增量升级/DDL回滚断言");

        SqliteConnection Raw() { var db=new SqliteConnection("Data Source="+path+";Pooling=False"); db.Open(); return db; }
        static object? Scalar(SqliteConnection db,string sql) { using var c=db.CreateCommand(); c.CommandText=sql; return c.ExecuteScalar(); }
        static void Sql(SqliteConnection db,string sql) { using var c=db.CreateCommand(); c.CommandText=sql; c.ExecuteNonQuery(); }
    }
}
