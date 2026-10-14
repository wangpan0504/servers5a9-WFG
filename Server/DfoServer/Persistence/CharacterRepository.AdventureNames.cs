using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.GameWorld.Adventure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public string ReadAdventureName(AccountOwners.Lease owner) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT CASE WHEN name_initialized=1 THEN name ELSE '' END FROM account_adventures WHERE account=$account";
        query.Parameters.AddWithValue("$account", owner.Account);
        return query.ExecuteScalar() as string ?? "";
    });

    public bool AdventureNameExists(AccountOwners.Lease owner, string name) => owner.Execute(() =>
    {
        using var db = database.Open();
        return AdventureNameExists(db, null, owner.Account, name);
    });

    static bool AdventureNameExists(SqliteConnection db, SqliteTransaction? tx, string account, string name)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT EXISTS(SELECT 1 FROM account_adventures WHERE name=$name COLLATE NOCASE AND account<>$account)";
        query.Parameters.AddWithValue("$name", name);
        query.Parameters.AddWithValue("$account", account);
        return (long)query.ExecuteScalar()! != 0;
    }

    public AdventureNameCommit CommitAdventureName(AccountOwners.Lease owner, AdventureNamePlan plan, AdventureCatalog catalog,
        DateTimeOffset now) => owner.Execute<AdventureNameCommit>(() =>
    {
        var allowed = plan.Before.State with { Name = plan.After.Name, NameInitialized = true };
        if (!AdventureName.IsValid(plan.After.Name) || JsonSerializer.Serialize(allowed) != JsonSerializer.Serialize(plan.After) ||
            plan.Inventory is not null && plan.Expected is null)
            throw new InvalidDataException("冒险团名称计划越界");
        plan.After.Validate(catalog);
        bool changed = plan.After != plan.Before.State;
        using var db = database.Open();
        // 名称占用复验与写入共享同一writer；先查询再独立提交会允许两个账号抢到相同名称。
        using var tx = db.BeginTransaction(deferred: !changed);
        var current = ReadAdventure(db, tx, owner.Account, catalog, now);
        if (JsonSerializer.Serialize(current.State) != JsonSerializer.Serialize(plan.Before.State)) return new(false, 19);
        if (!changed) return new(true, 0, current, plan.Expected);
        if (AdventureNameExists(db, tx, owner.Account, plan.After.Name)) return new(false, 20);
        var character = plan.Expected;
        if (plan.Inventory is { } inventory)
            character = CommitInventoryInTransaction(db, tx, owner, character!, inventory);
        WriteAdventureBalances(db, tx, owner.Account, current.State, plan.After, catalog);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$name", plan.After.Name);
        command.Parameters.AddWithValue("$revision", current.State.Revision + 1);
        command.CommandText = "UPDATE account_adventures SET name=$name,name_initialized=1 WHERE account=$account AND revision=$revision";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("冒险团名称版本失配");
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$event", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$character", (object?)character?.Id ?? DBNull.Value);
        command.Parameters.AddWithValue("$action", plan.Inventory is null ? "name_initialize" : "name_change");
        command.Parameters.AddWithValue("$before", current.State.Revision);
        command.Parameters.AddWithValue("$revision", current.State.Revision + 1);
        command.CommandText = "INSERT INTO account_adventure_receipts VALUES($account,$event,$character,$action,0,$before,$revision)";
        command.ExecuteNonQuery();
        var committed = ReadAdventure(db, tx, owner.Account, catalog, now);
        tx.Commit();
        return new(true, 0, committed, character);
    });
}
