using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IEndlessAdventureStore
{
    public EndlessAdventurePolicy? EndlessAdventure { get; set; }

    public int ReadEndlessAdventure(AccountOwners.Lease owner, int day) => owner.Execute(() =>
    {
        using var db = database.Open();
        return ReadEndlessAdventure(db, null, owner.Account, day);
    });

    static int ReadEndlessAdventure(SqliteConnection db, SqliteTransaction? tx, string account, int day)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT earned FROM account_endless_adventure_days WHERE account=$account AND day=$day";
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$day", day);
        return command.ExecuteScalar() is long count ? checked((int)count) : 0;
    }

    int GrantEndlessAdventure(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        Character expected, PreparedDungeonSettlement prepared)
    {
        if (prepared.EndlessAdventure is not { } reward || EndlessAdventure is not { } policy) return 0;
        if (!policy.Available) return 0;
        if (reward.Day != EndlessAdventurePolicy.Day(reward.At) || reward.Amount is <= 0 or > EndlessAdventurePolicy.DailyLimit)
            throw new InvalidDataException("无尽大冒险缺少原通关冻结额度");
        int before = ReadEndlessAdventure(db, tx, owner.Account, reward.Day);
        var wallet = ReadAccountWallet(db, owner.Account, tx);
        int available = Math.Max(0, policy.Coin.AccountMaterial!.HoldingLimit - wallet.Material(EndlessAdventurePolicy.CoinId).Balance);
        int amount = EndlessAdventurePolicy.Award((uint)reward.Amount, false, before, available);
        if (amount == 0) return 0;
        var balance = wallet.Material(EndlessAdventurePolicy.CoinId);
        var inventory = expected.Inventory with { AccountWallet = wallet };
        var after = inventory with { AccountWallet = wallet.SetMaterial(EndlessAdventurePolicy.CoinId,
            balance with { Balance = checked(balance.Balance + amount) }) };
        // 成长已完成唯一角色CAS，材料钱包参与原连接和事务，不能再调用独立CommitInventory。
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.CommandText = "INSERT INTO account_wallets(account) VALUES($account) ON CONFLICT(account) DO NOTHING";
        command.ExecuteNonQuery();
        WriteAccountMaterials(db, tx, owner, expected.Id, new(inventory, after, "endless_adventure"));
        command.Parameters.AddWithValue("$revision", wallet.Revision);
        command.CommandText = "UPDATE account_wallets SET revision=revision+1 WHERE account=$account AND revision=$revision";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("活动发币账号钱包版本变化");
        command.CommandText = """
            INSERT INTO account_endless_adventure_days(account,day,earned) VALUES($account,$day,$earned)
            ON CONFLICT(account,day) DO UPDATE SET earned=excluded.earned
            """;
        command.Parameters.AddWithValue("$day", reward.Day);
        command.Parameters.AddWithValue("$earned", before + amount);
        command.ExecuteNonQuery();
        return amount;
    }
}
