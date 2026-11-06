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

    // 只由持 server.lock、尚未监听的发行升级调用；所有活动与原版本在同一写事务中提交。
    internal void ResetDefaults(IReadOnlyList<ushort> eventIds)
    {
        if (eventIds.Distinct().Count() != eventIds.Count || eventIds.Any(id => !ServerEventService.IsRegistered(id)))
            throw new InvalidDataException("默认活动必须来自真实业务白名单");
        using var db = database.Open();
        using var write = db.BeginWrite("ServerEventRepository.ResetDefaults");
        using var tx = write.Transaction;
        foreach (ushort id in eventIds)
        {
            var before = Read(db, tx, id);
            before.Validate();
            if (before is { StartsAt: null, EndsAt: null, OverrideActive: true }) continue;
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                INSERT INTO server_events(event_id,revision,starts_at,ends_at,override_active) VALUES($id,$next,NULL,NULL,1)
                ON CONFLICT(event_id) DO UPDATE SET revision=$next,starts_at=NULL,ends_at=NULL,override_active=1
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$next", checked(before.Revision + 1));
            command.ExecuteNonQuery();
        }
        tx.Commit();
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
        using var write = db.BeginWrite("ServerEventRepository.Save", deferred: true);
        using var tx = write.Transaction;
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

    internal static ServerEventSchedule Read(SqliteConnection db, SqliteTransaction? tx, ushort eventId)
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
