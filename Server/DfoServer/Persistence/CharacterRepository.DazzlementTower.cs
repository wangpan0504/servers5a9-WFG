using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public DazzlementTowerState LoadDazzlementTower(AccountOwners.Lease owner) => owner.Execute(() =>
    {
        using var db = database.Open();
        return ReadDazzlementTower(db, null, owner.Account);
    });

    static DazzlementTowerState ReadDazzlementTower(SqliteConnection db, SqliteTransaction? tx, string account)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = """
            SELECT b.slot,b.week,b.character_id,b.last_entry_day,c.name,c.job
            FROM account_dazzlement_tower_bindings b LEFT JOIN characters c ON c.id=b.character_id AND c.account=b.account
            WHERE b.account=$account ORDER BY b.slot
            """;
        query.Parameters.AddWithValue("$account", account);
        using var reader = query.ExecuteReader();
        int week = 0;
        var bindings = ImmutableArray.CreateBuilder<DazzlementTowerBinding>();
        while (reader.Read())
        {
            if (week != 0 && week != reader.GetInt32(1)) throw new InvalidDataException("眩惑绑定周不一致");
            week = reader.GetInt32(1);
            if (!reader.IsDBNull(2) && reader.IsDBNull(4)) throw new InvalidDataException("眩惑绑定角色归属损坏");
            bindings.Add(new(reader.GetInt32(0), reader.IsDBNull(2) ? null : checked((ushort)reader.GetInt32(2)), reader.GetInt32(3),
                reader.IsDBNull(4) ? "" : reader.GetString(4), reader.IsDBNull(5) ? (byte)0 : checked((byte)reader.GetInt32(5))));
        }
        return new(week, bindings.ToImmutable());
    }

    static void AdmitDazzlementTower(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        Character expected, DungeonEntryCharge charge)
    {
        if (charge.Dazzlement is not { } admission) return;
        if (admission.Week <= 0 || admission.Day < 0 || charge.Identity.QuestId != 0)
            throw new InvalidDataException("眩惑绑定需要正式入场及有效周期");
        var state = ReadDazzlementTower(db, tx, owner.Account);
        if (state.Rejection(expected.Id, admission) is { } reason) throw new InvalidOperationException(reason);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$week", admission.Week);
        if (state.Week != admission.Week)
        {
            // 只在本账号首次新周入场时回收旧名单；删角留下的空槽本周仍占用名额。
            command.CommandText = "DELETE FROM account_dazzlement_tower_bindings WHERE account=$account AND week<$week";
            command.ExecuteNonQuery();
        }
        int slot = state.Week == admission.Week
            ? state.Bindings.SingleOrDefault(row => row.CharacterId == expected.Id)?.Slot ?? state.Bindings.Length + 1 : 1;
        command.Parameters.AddWithValue("$slot", slot);
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$day", admission.Day);
        command.CommandText = """
            INSERT INTO account_dazzlement_tower_bindings(account,slot,week,character_id,last_entry_day)
            VALUES($account,$slot,$week,$id,$day)
            ON CONFLICT(account,slot) DO UPDATE SET last_entry_day=excluded.last_entry_day
            WHERE account_dazzlement_tower_bindings.week=excluded.week
              AND account_dazzlement_tower_bindings.character_id=excluded.character_id
              AND account_dazzlement_tower_bindings.last_entry_day<excluded.last_entry_day
            """;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("眩惑绑定或当日资格已变化");
    }
}
