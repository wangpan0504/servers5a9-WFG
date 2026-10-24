using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static DungeonSpecialEntryUsage ReadSpecialEntryUsage(SqliteConnection db, SqliteTransaction? tx,
        string account, ushort characterId, int day, DungeonSpecialEntryKind kind)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT COALESCE(SUM(CASE WHEN character_id=$id THEN 1 ELSE 0 END),0),COUNT(DISTINCT character_id)
            FROM dungeon_special_entry_receipts WHERE account=$account AND service_day=$day AND kind=$kind
            """;
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$day", day);
        command.Parameters.AddWithValue("$kind", (int)kind);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("专用入场计数查询缺少结果");
        return new(checked((int)reader.GetInt64(0)), checked((int)reader.GetInt64(1)));
    }

    public DungeonSpecialEntryUsage LoadSpecialEntryUsage(AccountOwners.Lease owner, Character expected,
        int day, DungeonSpecialEntryKind kind) => owner.Execute(() =>
    {
        if (day is < 1 or > 3652058 || !Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException();
        using var db = database.Open();
        using var identity = db.CreateCommand();
        identity.CommandText = "SELECT count(*) FROM characters WHERE id=$id AND account=$account AND name=$name";
        identity.Parameters.AddWithValue("$id", expected.Id);
        identity.Parameters.AddWithValue("$account", owner.Account);
        identity.Parameters.AddWithValue("$name", expected.Name);
        if (Convert.ToInt64(identity.ExecuteScalar()) != 1)
            throw new InvalidOperationException("专用入场计数角色归属变化");
        return ReadSpecialEntryUsage(db, null, owner.Account, expected.Id, day, kind);
    });

    static void WriteSpecialEntry(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        Character expected, DungeonEntryCharge charge, long revision)
    {
        if (charge.Special is not { } special) return;
        special.Validate();
        bool matches = special.Kind switch
        {
            DungeonSpecialEntryKind.Blood => charge.DungeonId == 11006,
            DungeonSpecialEntryKind.Ultimate => charge.DungeonId == 11007,
            DungeonSpecialEntryKind.Despair => charge.DungeonId == 11007 + special.Layer,
            _ => false
        };
        if (!matches) throw new InvalidDataException("塔与祭坛入场类型、楼层与副本身份不一致");
        ValidateDespairEntry(db, tx, owner, expected, special);
        if (special.Expected != ReadSpecialEntryUsage(db, tx, owner.Account, expected.Id, special.Day, special.Kind))
            throw new InvalidOperationException("专用入场次数在准备后发生变化");
        long spent = charge.Items is { } inventory ? (long)inventory.Before.Gold - inventory.After.Gold : 0;
        if (spent != special.Gold) throw new InvalidDataException("专用入场金币与同事务背包计划不一致");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO dungeon_special_entry_receipts(character_id,account,instance_id,task_attempt,service_day,kind,dungeon_id,layer,gold,revision)
            VALUES($id,$account,$instance,$attempt,$day,$kind,$dungeon,$layer,$gold,$revision)
            """;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$instance", charge.Identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$attempt", charge.Identity.TaskAttempt);
        command.Parameters.AddWithValue("$day", special.Day);
        command.Parameters.AddWithValue("$kind", (int)special.Kind);
        command.Parameters.AddWithValue("$dungeon", charge.DungeonId);
        command.Parameters.AddWithValue("$layer", special.Layer);
        command.Parameters.AddWithValue("$gold", special.Gold);
        command.Parameters.AddWithValue("$revision", revision);
        command.ExecuteNonQuery();
    }
}
