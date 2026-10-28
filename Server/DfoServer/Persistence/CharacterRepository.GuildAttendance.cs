using DfoServer.Game.Accounts;
using DfoServer.Game.Guild;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IGuildAttendanceStore
{
    public bool ObserveGuildAttendance(AccountOwners.Lease owner, ushort characterId, long observedAtUnix) =>
        owner.Execute(() =>
        {
            if (observedAtUnix <= 0) return false;
            long day = GuildAttendanceDay.Start(observedAtUnix);
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.ObserveGuildAttendance");
            using var tx = write.Transaction;
            // 记录当前账号本次会籍的真实入城观察，不从在线历史回填出勤，不产生任何奖励。
            using var command = ManagementCommand(db, tx, """
                INSERT INTO guild_attendance(character_id,guild_id,joined_at_unix,business_day_unix,observed_at_unix)
                SELECT m.character_id,m.guild_id,m.joined_at_unix,$day,$observed
                FROM guild_members m JOIN characters c ON c.id=m.character_id
                WHERE c.id=$id AND c.account=$account AND m.joined_at_unix<=$observed
                ON CONFLICT(guild_id,business_day_unix,character_id,joined_at_unix) DO NOTHING
                """, ("$id", characterId), ("$account", owner.Account), ("$day", day), ("$observed", observedAtUnix));
            bool changed = command.ExecuteNonQuery() != 0;
            tx.Commit();
            return changed;
        });

    public GuildAttendanceView? ReadGuildAttendance(AccountOwners.Lease owner, ushort characterId, long nowUnix) =>
        owner.Execute(() =>
        {
            if (nowUnix <= 0) return null;
            using var db = database.Open();
            using var tx = db.BeginRead();
            using var command = ManagementCommand(db, tx, """
                SELECT m.guild_id,g.master_character_id FROM guild_members m
                JOIN characters c ON c.id=m.character_id JOIN guilds g ON g.guild_id=m.guild_id
                WHERE c.id=$id AND c.account=$account
                """, ("$id", characterId), ("$account", owner.Account));
            uint guild;
            ushort master;
            using (var row = command.ExecuteReader())
            {
                if (!row.Read()) return null;
                guild = checked((uint)row.GetInt64(0));
                master = checked((ushort)row.GetInt32(1));
            }
            long day = GuildAttendanceDay.Start(nowUnix);
            command.CommandText = """
                SELECT c.id,c.name,c.level,c.job,c.grow_type,m.grade,a.observed_at_unix
                FROM guild_attendance a JOIN guild_members m ON m.character_id=a.character_id
                  AND m.guild_id=a.guild_id AND m.joined_at_unix=a.joined_at_unix
                JOIN characters c ON c.id=m.character_id
                WHERE a.guild_id=$guild AND a.business_day_unix=$day AND a.observed_at_unix<=$now
                ORDER BY a.observed_at_unix,c.id
                """;
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$guild", guild);
            command.Parameters.AddWithValue("$day", day);
            command.Parameters.AddWithValue("$now", nowUnix);
            var members = new List<GuildAttendanceRow>();
            using (var row = command.ExecuteReader())
                while (row.Read())
                {
                    ushort id = checked((ushort)row.GetInt32(0));
                    members.Add(new(id, row.GetString(1), checked((ushort)row.GetInt32(2)), checked((byte)row.GetInt32(3)),
                        checked((byte)row.GetInt32(4)), GuildRoleRules.NormalizeGrade(id, master, checked((byte)row.GetInt32(5))),
                        row.GetInt64(6)));
                }
            return new GuildAttendanceView(guild, day, members.ToArray());
        });
}
