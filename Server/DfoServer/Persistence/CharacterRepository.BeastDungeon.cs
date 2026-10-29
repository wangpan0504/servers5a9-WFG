using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    void CommitBeastAdmission(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        DfoServer.Game.Characters.Character expected, DungeonEntryCharge charge)
    {
        if (charge.Beast is not { } admission) return;
        var current = Require(db, tx, owner.Account, expected.Name);
        int used = ReadBeastDungeonUsed(db, tx, expected.Id, owner.Account, admission.Day, charge.DungeonId);
        if (current.Id != expected.Id || !current.CompletedQuests.Contains(5927) ||
            used != admission.ExpectedUsed || !BeastDungeonRotation.CanEnter(admission.Day, charge.DungeonId, used))
            throw new InvalidOperationException("魔兽任务资格、开放图或入场次数已变化");
        if (admission.Ticket is not null)
        {
            ReserveBeastAdmission(db, tx, owner, expected, charge, admission);
            return;
        }
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO character_beast_dungeon_entries(character_id,account,day,dungeon_id,used)
            VALUES($id,$account,$day,$dungeon,$used)
            ON CONFLICT(character_id,day,dungeon_id) DO UPDATE SET used=excluded.used;
            """;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$day", admission.Day);
        command.Parameters.AddWithValue("$dungeon", BeastDungeonRotation.SourceDungeonId(charge.DungeonId));
        command.Parameters.AddWithValue("$used", used + 1);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("魔兽次数未提交");
    }

    public int LoadBeastDungeonUsed(AccountOwners.Lease owner, ushort characterId, int day, int dungeonId) => owner.Execute(() =>
    {
        using var db = database.Open();
        return ReadBeastDungeonUsed(db, null, characterId, owner.Account, day, dungeonId);
    });

    public int CommitBeastDungeonEntry(AccountOwners.Lease owner, ushort characterId, int day, int dungeonId) => owner.Execute(() =>
    {
        if (!BeastDungeonRotation.Contains(day, dungeonId)) return -1;
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitBeastDungeonEntry");
        using var tx = write.Transaction;
        int used = ReadBeastDungeonUsed(db, tx, characterId, owner.Account, day, dungeonId);
        if (!BeastDungeonRotation.CanEnter(day, dungeonId, used)) return -1;
        used++;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO character_beast_dungeon_entries(character_id,account,day,dungeon_id,used)
            VALUES($id,$account,$day,$dungeon,$used)
            ON CONFLICT(character_id,day,dungeon_id) DO UPDATE SET used=excluded.used,account=excluded.account;
            """;
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$day", day);
        command.Parameters.AddWithValue("$dungeon", BeastDungeonRotation.SourceDungeonId(dungeonId));
        command.Parameters.AddWithValue("$used", used);
        if (command.ExecuteNonQuery() != 1) return -1;
        tx.Commit();
        return used;
    });

    public int ResetBeastDungeonDay(AccountOwners.Lease owner, ushort characterId, int day) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            DELETE FROM character_beast_dungeon_entries
            WHERE character_id=$id AND account=$account AND day=$day;
            """;
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$day", day);
        return command.ExecuteNonQuery();
    });

    static int ReadBeastDungeonUsed(SqliteConnection db, SqliteTransaction? tx, ushort id, string account, int day, int dungeonId)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = """
            SELECT used FROM character_beast_dungeon_entries
            WHERE character_id=$id AND account=$account AND day=$day AND dungeon_id=$dungeon;
            """;
        query.Parameters.AddWithValue("$id", id);
        query.Parameters.AddWithValue("$account", account);
        query.Parameters.AddWithValue("$day", day);
        query.Parameters.AddWithValue("$dungeon", BeastDungeonRotation.SourceDungeonId(dungeonId));
        return query.ExecuteScalar() is long used ? checked((int)used) : 0;
    }
}
