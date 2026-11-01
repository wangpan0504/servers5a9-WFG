using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Items;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IAccountMaterialStore
{
    public Character ReconcileAccountMaterials(AccountOwners.Lease owner, Character expected, InventoryPlan legacy,
        IReadOnlyDictionary<int, AccountMaterialDefinition> limits, DateTimeOffset now) => owner.Execute(() =>
    {
        if (legacy.Reason != "account_material_legacy" || legacy.Before != expected.Inventory)
            throw new InvalidDataException("账号材料存量迁移身份不匹配");
        int week = AccountMaterialService.RaidWeek(now);
        var pending = limits.Where(pair => legacy.After.AccountWallet.Material(pair.Key).RaidWeek != week)
            .Select(pair => pair.Key).ToHashSet();
        if (!legacy.Changed && pending.Count == 0) return expected;
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.ReconcileAccountMaterials");
        using var tx = write.Transaction;
        var wallet = legacy.After.AccountWallet;
        var earned = pending.ToDictionary(id => id, _ => ImmutableSortedDictionary<ushort, int>.Empty);
        using (var command = db.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = "SELECT character_id,plan_json FROM character_raid_reward_receipts WHERE account=$account AND created_at>=$start AND created_at<$end";
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$start", AccountMaterialService.WeekStart(week).ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$end", AccountMaterialService.WeekStart(week).AddDays(7).ToUnixTimeSeconds());
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ushort character = checked((ushort)reader.GetInt32(0));
                var receipt = JsonSerializer.Deserialize<PreparedRaidReward>(reader.GetString(1))
                    ?? throw new InvalidDataException("历史攻坚材料奖励为空");
                if (receipt.Grants.IsDefault || receipt.Character != character)
                    throw new InvalidDataException("历史攻坚材料奖励身份无效");
                foreach (var grant in receipt.Grants.Where(row => pending.Contains(row.ItemId)))
                {
                    if (grant.Count <= 0) throw new InvalidDataException("历史攻坚材料数量无效");
                    var rows = earned[grant.ItemId];
                    earned[grant.ItemId] = rows.SetItem(character, checked(rows.GetValueOrDefault(character) + grant.Count));
                }
            }
        }
        foreach (int itemId in pending)
        {
            var rows = earned[itemId];
            var current = wallet.Material(itemId);
            wallet = wallet.SetMaterial(itemId, current with { RaidWeek = week, RaidEarned = checked((int)rows.Sum(row => (long)row.Value)), Characters = rows });
        }
        var plan = legacy.WithAfter(legacy.After with { AccountWallet = wallet });
        var result = CommitInventoryInTransaction(db, tx, owner, expected, plan);
        tx.Commit();
        return result;
    });

    static ImmutableSortedDictionary<int, AccountMaterialBalance> ReadAccountMaterials(SqliteConnection db, string account, SqliteTransaction? tx)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT item_id,balance,raid_week,raid_earned,character_earned FROM account_material_balances WHERE account=$account ORDER BY item_id";
        command.Parameters.AddWithValue("$account", account);
        using var reader = command.ExecuteReader();
        var values = ImmutableSortedDictionary.CreateBuilder<int, AccountMaterialBalance>();
        while (reader.Read())
        {
            var characters = JsonSerializer.Deserialize<ImmutableSortedDictionary<ushort, int>>(reader.GetString(4))
                ?? throw new InvalidDataException("账号材料角色额度为空");
            var value = new AccountMaterialBalance(reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), characters);
            value.Validate();
            values.Add(reader.GetInt32(0), value);
        }
        return values.ToImmutable();
    }

    static void WriteAccountMaterials(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner, ushort characterId, InventoryPlan plan)
    {
        if (!plan.AccountMaterialsChanged) return;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        foreach (int itemId in plan.Before.AccountWallet.Materials.Keys.Union(plan.After.AccountWallet.Materials.Keys))
        {
            var before = plan.Before.AccountWallet.Material(itemId);
            var after = plan.After.AccountWallet.Material(itemId);
            if (before.Same(after)) continue;
            after.Validate();
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$item", itemId);
            if (!plan.After.AccountWallet.Materials.ContainsKey(itemId))
            {
                command.CommandText = "DELETE FROM account_material_balances WHERE account=$account AND item_id=$item";
                command.ExecuteNonQuery();
            }
            else
            {
                command.Parameters.AddWithValue("$balance", after.Balance);
                command.Parameters.AddWithValue("$week", after.RaidWeek);
                command.Parameters.AddWithValue("$earned", after.RaidEarned);
                command.Parameters.AddWithValue("$characters", JsonSerializer.Serialize(after.CharacterEarned));
                command.CommandText = """
                    INSERT INTO account_material_balances(account,item_id,balance,raid_week,raid_earned,character_earned)
                    VALUES($account,$item,$balance,$week,$earned,$characters)
                    ON CONFLICT(account,item_id) DO UPDATE SET balance=excluded.balance,raid_week=excluded.raid_week,
                      raid_earned=excluded.raid_earned,character_earned=excluded.character_earned
                    """;
                command.ExecuteNonQuery();
            }
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$item", itemId);
            command.Parameters.AddWithValue("$id", characterId);
            command.Parameters.AddWithValue("$generation", owner.Generation);
            command.Parameters.AddWithValue("$action", plan.Reason);
            command.Parameters.AddWithValue("$before", JsonSerializer.Serialize(before));
            command.Parameters.AddWithValue("$after", JsonSerializer.Serialize(after));
            command.CommandText = """
                INSERT INTO account_material_audit(account,character_id,generation,action_name,item_id,before_state,after_state)
                VALUES($account,$id,$generation,$action,$item,$before,$after)
                """;
            command.ExecuteNonQuery();
        }
    }
}
