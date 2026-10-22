using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public AccountWallet LoadAccountWallet(AccountOwners.Lease owner) => owner.Execute(() =>
    {
        using var db = database.Open();
        return ReadAccountWallet(db, owner.Account);
    });
    static AccountWallet ReadAccountWallet(SqliteConnection db, string account, SqliteTransaction? transaction = null)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$account", account);
        command.CommandText = "SELECT revision,rental_points,premium_contracts,seria_luck,character_slots,mileage FROM account_wallets WHERE account=$account";
        long revision = 0;
        int rentalPoints = 0;
        int seriaLuck = 0;
        int mileage = 0;
        ushort characterSlots = 32;
        var contracts = ImmutableSortedDictionary.CreateBuilder<int, long>();
        using (var walletReader = command.ExecuteReader())
            if (walletReader.Read())
            {
                revision = walletReader.GetInt64(0);
                rentalPoints = walletReader.GetInt32(1);
                characterSlots = checked((ushort)walletReader.GetInt32(4));
                mileage = walletReader.GetInt32(5);
                if (!walletReader.IsDBNull(2))
                {
                    var persisted = JsonSerializer.Deserialize<Dictionary<int, long>>(walletReader.GetString(2));
                    if (persisted is not null)
                        foreach (var pair in persisted)
                            contracts.Add(pair.Key, pair.Value);
                }
                if (!walletReader.IsDBNull(3))
                    seriaLuck = walletReader.GetInt32(3);
            }
        command.CommandText = "SELECT currency_kind,balance FROM account_currency_balances WHERE account=$account ORDER BY currency_kind";
        var balances = ImmutableSortedDictionary.CreateBuilder<CurrencyKind, int>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) balances.Add((CurrencyKind)reader.GetInt32(0), reader.GetInt32(1));
        var wallet = new AccountWallet(revision, balances.ToImmutable(), rentalPoints, seriaLuck, contracts.ToImmutable()) { CharacterSlotCapacity = characterSlots, Mileage = mileage };
        wallet.Validate();
        return wallet;
    }

    static InventorySnapshot WriteWallet(SqliteConnection db, SqliteTransaction transaction, AccountOwners.Lease owner,
        ushort characterId, InventoryPlan plan, InventorySnapshot after)
    {
        var before = plan.Before;
        string reason = plan.Reason;
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE characters SET revive_coins=$coins,win_points=$wins,gold_carry_limit=$carry,auction_gold_limit=$auction WHERE id=$id AND account=$account;
            """;
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$coins", after.ReviveCoins);
        command.Parameters.AddWithValue("$wins", after.WinPoints);
        command.Parameters.AddWithValue("$carry", after.GoldCarryLimit);
        command.Parameters.AddWithValue("$auction", after.AuctionGoldLimit);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("钱包角色所有权变化");
        var changes = plan.ChangedCurrencies;
        bool contractsChanged = !before.AccountWallet.PremiumContracts.SequenceEqual(after.AccountWallet.PremiumContracts);
        bool seriaLuckChanged = before.AccountWallet.SeriaLuck != after.AccountWallet.SeriaLuck;
        if (before.AccountWallet.CharacterSlotCapacity != after.AccountWallet.CharacterSlotCapacity || changes.Any(CurrencyWallet.IsAccount) || contractsChanged || seriaLuckChanged)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$revision", before.AccountWallet.Revision);
            command.CommandText = "INSERT INTO account_wallets(account) VALUES($account) ON CONFLICT(account) DO NOTHING";
            command.ExecuteNonQuery();
            command.Parameters.AddWithValue("$rental", after.AccountWallet.RentalPoints);
            command.Parameters.AddWithValue("$mileage", after.AccountWallet.Mileage);
            command.Parameters.AddWithValue("$slots", after.AccountWallet.CharacterSlotCapacity);
            command.Parameters.AddWithValue("$contracts", JsonSerializer.Serialize(after.AccountWallet.PremiumContracts));
            command.Parameters.AddWithValue("$luck", after.AccountWallet.SeriaLuck);
            command.CommandText = "UPDATE account_wallets SET revision=revision+1,rental_points=$rental,mileage=$mileage,character_slots=$slots,premium_contracts=$contracts,seria_luck=$luck WHERE account=$account AND revision=$revision";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("账号钱包版本变化");
            command.CommandText = "DELETE FROM account_currency_balances WHERE account=$account";
            command.ExecuteNonQuery();
            command.Parameters.AddWithValue("$kind", 0);
            command.Parameters.AddWithValue("$balance", 0);
            command.CommandText = "INSERT INTO account_currency_balances(account,currency_kind,balance) VALUES($account,$kind,$balance)";
            foreach (var pair in after.AccountWallet.Balances)
            {
                command.Parameters["$kind"].Value = (int)pair.Key;
                command.Parameters["$balance"].Value = pair.Value;
                command.ExecuteNonQuery();
            }
            after = after with { AccountWallet = after.AccountWallet with { Revision = checked(before.AccountWallet.Revision + 1) } };
        }
        foreach (var kind in changes)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$id", characterId);
            command.Parameters.AddWithValue("$generation", owner.Generation);
            command.Parameters.AddWithValue("$action", reason);
            command.Parameters.AddWithValue("$kind", (int)kind);
            command.Parameters.AddWithValue("$scope", CurrencyWallet.IsAccount(kind) ? 1 : 0);
            command.Parameters.AddWithValue("$before", CurrencyWallet.Get(before, kind));
            command.Parameters.AddWithValue("$after", CurrencyWallet.Get(after, kind));
            command.CommandText = """
                INSERT INTO currency_audit_log(account,character_id,generation,action_name,currency_kind,account_scope,balance_before,balance_after)
                VALUES($account,$id,$generation,$action,$kind,$scope,$before,$after);
                """;
            command.ExecuteNonQuery();
        }
        return after;
    }
}
