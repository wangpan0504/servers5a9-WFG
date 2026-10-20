using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static CrackDailyState ReadCrackDaily(SqliteConnection db, SqliteTransaction? tx, ushort id, string account, int day)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT day,clear_count,daily_reward FROM character_crack_daily WHERE character_id=$id AND account=$account";
        query.Parameters.AddWithValue("$id", id);
        query.Parameters.AddWithValue("$account", account);
        using var reader = query.ExecuteReader();
        return reader.Read() && reader.GetInt64(0) == day
            ? new(reader.GetInt32(1), reader.GetInt32(2) != 0)
            : default;
    }

    public CrackDailyState LoadCrackDaily(AccountOwners.Lease owner, ushort characterId, int day) => owner.Execute(() =>
    {
        using var db = database.Open();
        return ReadCrackDaily(db, null, characterId, owner.Account, day);
    });

    public CrackDailyCommit CommitCrackClear(AccountOwners.Lease owner, Character expected, int day, CrackDailyState state, InventoryPlan? reward) =>
        owner.Execute(() =>
        {
            if (state.Clears < 0) throw new InvalidDataException("次元裂缝当日通关次数无效");
            using var db = database.Open();
            using var tx = db.BeginTransaction(deferred: false);
            var character = reward is null ? expected : CommitInventoryInTransaction(db, tx, owner, expected, reward);
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                INSERT INTO character_crack_daily(character_id,account,day,clear_count,daily_reward)
                VALUES($id,$account,$day,$clear,$daily)
                ON CONFLICT(character_id) DO UPDATE SET account=excluded.account,day=excluded.day,
                  clear_count=excluded.clear_count,daily_reward=excluded.daily_reward;
                """;
            command.Parameters.AddWithValue("$id", expected.Id);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$day", day);
            command.Parameters.AddWithValue("$clear", state.Clears);
            command.Parameters.AddWithValue("$daily", state.DailyGranted ? 1 : 0);
            command.ExecuteNonQuery();
            tx.Commit();
            return new CrackDailyCommit(character, state);
        });
}
