using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public EkernCycleProgress LoadEkernCycle(AccountOwners.Lease owner, ushort characterId, int day) => owner.Execute(() =>
    {
        using var db = database.Open();
        return ReadEkernCycle(db,null,owner.Account,characterId,day);
    });
    static EkernCycleProgress ReadEkernCycle(SqliteConnection db, SqliteTransaction? tx, string account, ushort characterId, int day)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT service_day,normal_clears,trial_clears FROM character_ekern_cycle WHERE character_id=$id AND account=$account";
        query.Parameters.AddWithValue("$id",characterId); query.Parameters.AddWithValue("$account",account);
        using var reader = query.ExecuteReader();
        return reader.Read() && reader.GetInt32(0) >= day ? new(reader.GetInt32(0),reader.GetInt32(1),reader.GetInt32(2)) : new(day);
    }
    static void WriteEkernClear(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner, ushort id,
        uint dungeon, int day)
    {
        var before = ReadEkernCycle(db,tx,owner.Account,id,day);
        var after = dungeon == 5097 ? before with { Normal = Math.Min(3,before.Normal+1) } : before with { Trial = Math.Min(1,before.Trial+1) };
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            INSERT INTO character_ekern_cycle(character_id,account,service_day,normal_clears,trial_clears)
            VALUES($id,$account,$day,$normal,$trial)
            ON CONFLICT(character_id) DO UPDATE SET service_day=excluded.service_day,normal_clears=excluded.normal_clears,trial_clears=excluded.trial_clears
            """;
        command.Parameters.AddWithValue("$id",id); command.Parameters.AddWithValue("$account",owner.Account);
        command.Parameters.AddWithValue("$day",after.Day); command.Parameters.AddWithValue("$normal",after.Normal); command.Parameters.AddWithValue("$trial",after.Trial);
        command.ExecuteNonQuery();
    }
}
