using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IPvpTotalTeamStore
{
    public bool IsPvpTotalTeamNameTaken(AccountOwners.Lease owner, string name) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT EXISTS(SELECT 1 FROM account_pvp_total_teams WHERE name=$name AND account<>$account)";
        query.Parameters.AddWithValue("$name", name);
        query.Parameters.AddWithValue("$account", owner.Account);
        return query.ExecuteScalar() is 1L;
    });

    public PvpTotalTeam? ReadPvpTotalTeam(AccountOwners.Lease owner) => owner.Execute(() =>
    {
        using var db = database.Open();
        return ReadPvpTotalTeam(db, null, owner.Account);
    });

    static PvpTotalTeam? ReadPvpTotalTeam(SqliteConnection db, SqliteTransaction? tx, string account)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT team_id,revision,name,first_id,second_id,third_id FROM account_pvp_total_teams WHERE account=$account";
        query.Parameters.AddWithValue("$account", account);
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return null;
        ushort? Character(int at) => reader.IsDBNull(at) ? null : checked((ushort)reader.GetInt32(at));
        return new(Guid.ParseExact(reader.GetString(0), "N"), reader.GetInt64(1), reader.GetString(2),
            [Character(3), Character(4), Character(5)]);
    }

    public PvpTotalTeam? SavePvpTotalTeam(AccountOwners.Lease owner, PvpTotalTeam? expected, PvpTotalTeam desired,
        ImmutableArray<CharacterRosterEntry> characters) => owner.Execute(() =>
    {
        if (desired.Id == Guid.Empty || !PvpTotalTeam.ValidName(desired.Name) || !desired.Complete ||
            desired.Characters.Distinct().Count() != 3 || characters.IsDefault || characters.Length != 3 ||
            !desired.Characters.SequenceEqual(characters.Select(row => (ushort?)row.Id)) ||
            desired.Revision != (expected?.Revision ?? 0) ||
            expected is not null && (desired.Id != expected.Id || desired.Name != expected.Name))
            throw new InvalidOperationException("总力编队规划身份无效");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.SavePvpTotalTeam");
        using var tx = write.Transaction;
        var current = ReadPvpTotalTeam(db, tx, owner.Account);
        if (expected is null ? current is not null : current is null || !current.SameLineup(expected))
            throw new InvalidOperationException("总力编队版本已变化");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account);
        // 名册只读身份在同一提交事务内复验，不能把角色换槽或改名后的旧索引保存成新角色。
        command.CommandText = "SELECT slot,name,job,grow_type,level FROM characters WHERE account=$account AND id=$id";
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        foreach (var character in characters)
        {
            id.Value = character.Id;
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetInt32(0) != character.Slot || reader.GetString(1) != character.Name ||
                reader.GetInt32(2) != character.Job || reader.GetInt32(3) != character.GrowType || reader.GetInt32(4) != character.Level)
                throw new InvalidOperationException("总力编队角色归属、身份或资格已变化");
        }
        if (current is not null && current.Characters.SequenceEqual(desired.Characters))
        {
            tx.Commit();
            return current;
        }
        command.Parameters.AddWithValue("$name", desired.Name);
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM account_pvp_total_teams WHERE name=$name AND account<>$account)";
        if (command.ExecuteScalar() is 1L) return null;
        command.Parameters.AddWithValue("$team", desired.Id.ToString("N"));
        long revision = checked(desired.Revision + 1);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$before", desired.Revision);
        command.Parameters.AddWithValue("$first", desired.Characters[0]!.Value);
        command.Parameters.AddWithValue("$second", desired.Characters[1]!.Value);
        command.Parameters.AddWithValue("$third", desired.Characters[2]!.Value);
        command.CommandText = """
            INSERT INTO account_pvp_total_teams(account,team_id,revision,name,first_id,second_id,third_id)
            VALUES($account,$team,$revision,$name,$first,$second,$third)
            ON CONFLICT(account) DO UPDATE SET revision=excluded.revision,
                first_id=excluded.first_id,second_id=excluded.second_id,third_id=excluded.third_id
            WHERE account_pvp_total_teams.team_id=$team AND account_pvp_total_teams.revision=$before
            """;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("总力编队提交冲突");
        tx.Commit();
        return desired with { Revision = revision };
    });

    static void RemovePvpTotalCharacter(SqliteConnection db, SqliteTransaction tx, string account, ushort character)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            UPDATE account_pvp_total_teams SET revision=revision+1,
                first_id=CASE WHEN first_id=$id THEN NULL ELSE first_id END,
                second_id=CASE WHEN second_id=$id THEN NULL ELSE second_id END,
                third_id=CASE WHEN third_id=$id THEN NULL ELSE third_id END
            WHERE account=$account AND (first_id=$id OR second_id=$id OR third_id=$id)
            """;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$id", character);
        command.ExecuteNonQuery();
    }
}
