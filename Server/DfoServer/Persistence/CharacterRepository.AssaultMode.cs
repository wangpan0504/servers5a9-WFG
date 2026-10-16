using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static AssaultDailyCounts ReadAssaultDaily(SqliteConnection db, SqliteTransaction? tx, ushort id, string account, int day)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT day,clear_count,hidden_count FROM character_assault_daily WHERE character_id=$id AND account=$account";
        query.Parameters.AddWithValue("$id", id);
        query.Parameters.AddWithValue("$account", account);
        using var reader = query.ExecuteReader();
        // 跨日即视为重置；旧日行在下一次通关时覆盖。
        return reader.Read() && reader.GetInt64(0) == day ? new(reader.GetInt32(1), reader.GetInt32(2)) : default;
    }

    public AssaultDailyCounts LoadAssaultDaily(AccountOwners.Lease owner, ushort characterId, int day) => owner.Execute(() =>
    {
        using var db = database.Open();
        return ReadAssaultDaily(db, null, characterId, owner.Account, day);
    });

    public AssaultDailyCommit CommitAssaultClear(AccountOwners.Lease owner, Character expected, int day, bool hidden, InventoryPlan? reward) =>
        owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        var counts = AssaultModeEntryPolicy.AfterClear(ReadAssaultDaily(db, tx, expected.Id, owner.Account, day), hidden);
        var character = reward is null ? expected : CommitInventoryInTransaction(db, tx, owner, expected, reward);
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            INSERT INTO character_assault_daily(character_id,account,day,clear_count,hidden_count)
            VALUES($id,$account,$day,$clear,$hidden)
            ON CONFLICT(character_id) DO UPDATE SET account=excluded.account,day=excluded.day,
              clear_count=excluded.clear_count,hidden_count=excluded.hidden_count;
            """;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$day", day);
        command.Parameters.AddWithValue("$clear", counts.Clears);
        command.Parameters.AddWithValue("$hidden", counts.Hidden);
        command.ExecuteNonQuery();
        tx.Commit();
        return new AssaultDailyCommit(character, counts);
    });
}
