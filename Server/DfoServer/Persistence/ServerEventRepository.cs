using DfoServer.Game.Events;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed class ServerEventRepository(string path) : IServerEventStore
{
    readonly SqliteGameDatabase database = new(path);

    public ServerEventSchedule Read(ushort eventId)
    {
        using var db = database.Open();
        return Read(db, null, eventId);
    }

    public ServerEventSchedule Save(ServerEventSchedule expected, ServerEventSchedule desired)
    {
        expected.Validate();
        desired.Validate();
        if (desired.EventId != expected.EventId || desired.Revision != expected.Revision)
            throw new InvalidDataException("活动配置不属于原版本");
        using var db = database.Open();
        if (Read(db, null, expected.EventId) != expected)
            throw new InvalidOperationException("活动配置已改变，请重新读取");
        if (desired == expected)
            return expected;
        if (expected.Revision == long.MaxValue)
            throw new InvalidDataException("活动配置版本已耗尽");
        using var tx = db.BeginTransaction(deferred: true);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.EventId);
        command.Parameters.AddWithValue("$revision", expected.Revision);
        command.Parameters.AddWithValue("$next", checked(expected.Revision + 1));
        command.Parameters.AddWithValue("$start", (object?)desired.StartsAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$end", (object?)desired.EndsAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$override", desired.OverrideActive is { } active ? active ? 1 : 0 : DBNull.Value);
        command.CommandText = expected.Revision == 0
            ? "INSERT INTO server_events(event_id,revision,starts_at,ends_at,override_active) VALUES($id,$next,$start,$end,$override) ON CONFLICT DO NOTHING"
            : "UPDATE server_events SET revision=$next,starts_at=$start,ends_at=$end,override_active=$override WHERE event_id=$id AND revision=$revision";
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("活动配置已改变，请重新读取");
        var committed = Read(db, tx, expected.EventId);
        tx.Commit();
        return committed;
    }

    static ServerEventSchedule Read(SqliteConnection db, SqliteTransaction? tx, ushort eventId)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT revision,starts_at,ends_at,override_active FROM server_events WHERE event_id=$id";
        command.Parameters.AddWithValue("$id", eventId);
        using var reader = command.ExecuteReader();
        return !reader.Read() ? new(eventId) : new(eventId, reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3) != 0);
    }
}
