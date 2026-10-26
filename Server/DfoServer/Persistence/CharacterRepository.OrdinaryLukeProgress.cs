using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Characters;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public OrdinaryLukeProgress LoadOrdinaryLukeProgress(AccountOwners.Lease owner, ushort characterId) => owner.Execute(() =>
    {
        using var db = database.Open();
        return ReadOrdinaryLukeProgress(db, null, owner.Account, characterId);
    });

    static OrdinaryLukeProgress ReadOrdinaryLukeProgress(SqliteConnection db, SqliteTransaction? tx, string account, ushort characterId)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT day,progress FROM character_luke_normal_progress WHERE character_id=$id AND account=$account";
        query.Parameters.AddWithValue("$id", characterId);
        query.Parameters.AddWithValue("$account", account);
        using var reader = query.ExecuteReader();
        return reader.Read() ? new(reader.GetInt32(0), reader.GetInt32(1)) : default;
    }

    static void WriteOrdinaryLukeProgress(SqliteConnection db, SqliteTransaction tx, string account, ushort characterId, OrdinaryLukeProgress progress)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO character_luke_normal_progress(character_id,account,day,progress) VALUES($id,$account,$day,$progress)
            ON CONFLICT(character_id) DO UPDATE SET day=excluded.day,progress=excluded.progress
            WHERE character_luke_normal_progress.account=excluded.account AND character_luke_normal_progress.day<=excluded.day
            """;
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$day", progress.Day);
        command.Parameters.AddWithValue("$progress", progress.Completed);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("普通卢克进度归属或业务日已变化");
    }

    static void AdmitOrdinaryLuke(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner, Character expected, DungeonEntryCharge charge)
    {
        if (charge.OrdinaryLukeEntry is not { } entry) return;
        // 正式Run计划已冻结入场预留完成后的revision；原费用事务仍以expected旧revision做CAS。
        if (!OrdinaryLukeProgress.Applies(entry) || entry.OrdinaryLukeDay is not { } day ||
            entry.CharacterId != expected.Id || entry.CharacterRevision != checked(expected.Revision + 1) ||
            entry.DungeonId != charge.DungeonId || entry.StartMap.Id != charge.MapId || entry.QuestId != charge.Identity.QuestId)
            throw new InvalidDataException("普通卢克准入必须携带当前角色原资源计划与冻结业务日");
        var before = ReadOrdinaryLukeProgress(db, tx, owner.Account, charge.Identity.CharacterId);
        if (!before.Allows(day, checked((uint)charge.DungeonId)))
            throw new InvalidOperationException("普通卢克前序未完成或业务日已回拨");
        WriteOrdinaryLukeProgress(db, tx, owner.Account, charge.Identity.CharacterId,
            new(day, before.Day == day ? before.Completed : 0));
    }

    static void ClearOrdinaryLuke(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner, SinglePlayerDungeonRun run)
    {
        if (run.Entry.OrdinaryLukeDay is not { } day) return;
        if (!OrdinaryLukeProgress.Applies(run.Entry)) throw new InvalidDataException("普通卢克进度缺少正式资源身份");
        ushort id = run.Snapshot.Identity.CharacterId;
        var before = ReadOrdinaryLukeProgress(db, tx, owner.Account, id);
        if (before.Day < day) throw new InvalidOperationException("普通卢克通关缺少已提交准入日");
        var after = before.Clear(day, run.DungeonId);
        if (after != before) WriteOrdinaryLukeProgress(db, tx, owner.Account, id, after);
    }
}
