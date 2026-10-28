using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IGuildApplicationsStore
{
    const string DirectorySelect = """
        SELECT g.guild_id,g.name,c.name,g.level,
            (SELECT count(*) FROM guild_members m WHERE m.guild_id=g.guild_id),
            COALESCE(s.join_mode,0),COALESCE(s.tags,0),COALESCE(s.promotion,''),COALESCE(a.mark,0),g.tier,
            g.created_at_unix,COALESCE(e.member_limit,300),
            (SELECT count(*) FROM guild_attendance attendance JOIN guild_members active
              ON active.character_id=attendance.character_id AND active.guild_id=attendance.guild_id
                AND active.joined_at_unix=attendance.joined_at_unix
              WHERE attendance.guild_id=g.guild_id AND attendance.business_day_unix=$day
                AND attendance.observed_at_unix<=$now)
        FROM guilds g JOIN characters c ON c.id=g.master_character_id
        LEFT JOIN guild_settings s ON s.guild_id=g.guild_id
        LEFT JOIN guild_appearance a ON a.guild_id=g.guild_id
        LEFT JOIN guild_economy e ON e.guild_id=g.guild_id
        """;

    static GuildDirectoryEntry DirectoryEntry(SqliteDataReader row) => new(
        checked((uint)row.GetInt64(0)), row.GetString(1), row.GetString(2), checked((byte)row.GetInt32(3)),
        checked((ushort)row.GetInt32(4)), checked((byte)row.GetInt32(5)), checked((uint)row.GetInt64(6)), row.GetString(7))
        {
            Mark = checked((uint)row.GetInt64(8)), Tier = checked((byte)row.GetInt32(9)),
            CreatedAtUnix = checked((uint)row.GetInt64(10)), MemberLimit = checked((uint)row.GetInt32(11)),
            AttendanceCount = checked((uint)row.GetInt64(12))
        };

    static GuildDirectoryEntry? ReadApplicationDirectory(SqliteConnection db, SqliteTransaction? transaction,
        uint? id = null, string? name = null)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var command = ManagementCommand(db, transaction,
            DirectorySelect + (id is not null ? " WHERE g.guild_id=$value" : " WHERE g.name=$value"),
            ("$value", id is { } guild ? guild : name!), ("$day", GuildAttendanceDay.Start(now)), ("$now", now));
        using var row = command.ExecuteReader();
        return row.Read() ? DirectoryEntry(row) : null;
    }

    public GuildDirectoryEntry? FindApplicationGuild(string name)
    {
        using var db = database.Open();
        return ReadApplicationDirectory(db, null, name: name);
    }

    public GuildSearchPage SearchGuilds(GuildSearchFilter filter)
    {
        if (!filter.IsValid) throw new InvalidDataException("Invalid guild search filter");
        using var db = database.Open();
        using var transaction = db.BeginRead();
        string where = filter.Mode switch
        {
            0 => " WHERE instr(lower(g.name),lower($query))>0",
            1 => " WHERE instr(lower(c.name),lower($query))>0",
            _ => " WHERE (COALESCE(s.tags,0)&$tags)=$tags"
        };
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var command = ManagementCommand(db, transaction, """
            SELECT count(*) FROM guilds g JOIN characters c ON c.id=g.master_character_id
            LEFT JOIN guild_settings s ON s.guild_id=g.guild_id
            """ + where, ("$query", filter.Query.Trim()), ("$tags", filter.Tags),
            ("$day", GuildAttendanceDay.Start(now)), ("$now", now));
        uint total = checked((uint)Convert.ToInt64(command.ExecuteScalar()));
        command.CommandText = DirectorySelect + where + " ORDER BY g.level DESC,g.guild_id LIMIT 30 OFFSET $offset";
        command.Parameters.AddWithValue("$offset", filter.Page * 30);
        var guilds = new List<GuildDirectoryEntry>();
        using var rows = command.ExecuteReader();
        while (rows.Read()) guilds.Add(DirectoryEntry(rows));
        return new(total, filter.Page, guilds.ToArray());
    }

    public GuildAccountDirectory ReadAccountGuildDirectory(AccountOwners.Lease owner, ushort selectedId, byte region) =>
        owner.Execute(() =>
        {
            using var db = database.Open();
            using var transaction = db.BeginRead();
            var memberships = new List<(string Name, uint Guild)>();
            var applications = new List<(string Name, uint Guild)>();
            using var command = ManagementCommand(db, transaction, """
                SELECT c.name,m.guild_id FROM characters c JOIN guild_members m ON m.character_id=c.id
                WHERE c.account=$account ORDER BY c.slot
                """, ("$account", owner.Account));
            using (var rows = command.ExecuteReader())
                while (rows.Read()) memberships.Add((rows.GetString(0), checked((uint)rows.GetInt64(1))));
            command.CommandText = """
                SELECT c.name,a.guild_id FROM characters c JOIN guild_applications a ON a.character_id=c.id
                WHERE c.account=$account AND c.id=$id
                    AND NOT EXISTS(SELECT 1 FROM guild_members m WHERE m.character_id=c.id)
                ORDER BY a.created_at_unix,a.guild_id
                """;
            command.Parameters.AddWithValue("$id", selectedId);
            using (var rows = command.ExecuteReader())
                while (rows.Read()) applications.Add((rows.GetString(0), checked((uint)rows.GetInt64(1))));
            var ids = memberships.Concat(applications).Select(value => value.Guild).Distinct();
            var directory = new Dictionary<uint, GuildDirectoryEntry>();
            foreach (uint id in ids)
                if (ReadApplicationDirectory(db, transaction, id) is { } guild) directory.Add(id, guild);
            return new GuildAccountDirectory(directory.Values.ToArray(),
                memberships.Where(value => directory.ContainsKey(value.Guild))
                    .Select(value => new GuildAccountEntry(value.Name, directory[value.Guild], false, region)).ToArray(),
                applications.Where(value => directory.ContainsKey(value.Guild))
                    .Select(value => new GuildAccountEntry(value.Name, directory[value.Guild], true, region)).ToArray());
        });

    public GuildApplicationList ReadGuildApplications(GuildManagementIdentity actor) => actor.Owner.Execute(() =>
    {
        if (!actor.IsCurrent()) return new GuildApplicationList(36, []);
        using var db = database.Open();
        using var transaction = db.BeginRead();
        var state = ReadManagement(db, transaction, actor.Owner.Account, actor.CharacterId);
        if (state is null || state.Grade > 2) return new(36, []);
        using var command = ManagementCommand(db, transaction, """
            SELECT c.id,c.account,c.name,c.job,c.grow_type,c.level,a.message,a.created_at_unix,a.region
            FROM guild_applications a JOIN characters c ON c.id=a.character_id
            WHERE a.guild_id=$guild AND NOT EXISTS(SELECT 1 FROM guild_members m WHERE m.character_id=c.id)
            ORDER BY a.created_at_unix,c.id
            """, ("$guild", state.Guild.Id));
        using var rows = command.ExecuteReader();
        var applications = new List<GuildApplicationRow>();
        while (rows.Read()) applications.Add(new(checked((ushort)rows.GetInt32(0)), rows.GetString(1), rows.GetString(2),
            checked((byte)rows.GetInt32(3)), checked((byte)rows.GetInt32(4)), checked((byte)rows.GetInt32(5)),
            rows.GetString(6), checked((uint)rows.GetInt64(7)), checked((byte)rows.GetInt32(8))));
        return new(0, applications.ToArray());
    });

    public GuildApplicationCommit ApplyToGuild(GuildManagementIdentity actor, string name, string message,
        byte region, Func<long> now, int memberLimit) => actor.Owner.Execute(() =>
    {
        if (!actor.IsCurrent() || region != actor.Channel.ServerIndex || region >= 250 || !GuildApplications.ValidMessage(message))
            return new GuildApplicationCommit(23);
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.ApplyToGuild");
        using var transaction = write.Transaction;
        if (!actor.IsCurrent()) return new(3);
        long at = now();
        var guild = ReadApplicationDirectory(db, transaction, name: name);
        if (guild is null) return new(23);
        var current = ReadManagement(db, transaction, actor.Owner.Account, actor.CharacterId);
        if (current is not null)
            return current.Guild.Id == guild.Id ? new(0, guild, true, current.Revision, current.Members) : new(39);
        using var identity = ManagementCommand(db, transaction,
            "SELECT name FROM characters WHERE id=$id AND account=$account",
            ("$id", actor.CharacterId), ("$account", actor.Owner.Account));
        if (identity.ExecuteScalar() as string != actor.Name) return new(3);
        if (ApplicationCooldown(db, transaction, actor.CharacterId, at)) return new(103);
        if (guild.JoinMode == 1)
        {
            if (guild.Members >= GuildMemberLimit(db, transaction, guild.Id, memberLimit)) return new(38);
            InsertApplicationMember(db, transaction, guild.Id, actor.CharacterId, at);
            RemoveAllGuildApplications(db, transaction, actor.CharacterId, guild.Id);
            long revision = TouchManagement(db, transaction, guild.Id);
            var joined = ReadManagement(db, transaction, actor.Owner.Account, actor.CharacterId)!;
            guild = ReadApplicationDirectory(db, transaction, guild.Id)!;
            transaction.Commit();
            return new(0, guild, true, revision, joined.Members);
        }
        using var command = ManagementCommand(db, transaction,
            "SELECT message,region FROM guild_applications WHERE guild_id=$guild AND character_id=$id",
            ("$guild", guild.Id), ("$id", actor.CharacterId));
        using (var row = command.ExecuteReader())
            if (row.Read()) return row.GetString(0) == message && row.GetInt32(1) == region ? new(0, guild) : new(23);
        // 两个原生集合的计数为u8；拒绝不可完整投影的新请求，不静默截断既有申请。
        command.CommandText = """
            SELECT count(DISTINCT guild_id) FROM (
                SELECT guild_id FROM guild_applications WHERE character_id=$id
                UNION ALL SELECT m.guild_id FROM guild_members m JOIN characters c ON c.id=m.character_id
                    WHERE c.account=$account)
            """;
        command.Parameters.AddWithValue("$account", actor.Owner.Account);
        if (Convert.ToInt64(command.ExecuteScalar()) >= 255 || !ApplicationListFits(db, transaction, guild.Id, actor.Name, message))
            return new(38);
        command.CommandText = """
            INSERT INTO guild_applications(guild_id,character_id,message,region,created_at_unix)
            VALUES($guild,$id,$message,$region,$created)
            """;
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$region", region);
        command.Parameters.AddWithValue("$created", at);
        command.ExecuteNonQuery();
        long updated = TouchManagement(db, transaction, guild.Id);
        transaction.Commit();
        return new(0, guild, Revision: updated);
    });

    public GuildApplicationCommit CancelGuildApplication(GuildManagementIdentity actor, uint guildId) => actor.Owner.Execute(() =>
    {
        if (!actor.IsCurrent()) return new GuildApplicationCommit(3);
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CancelGuildApplication");
        using var transaction = write.Transaction;
        if (!actor.IsCurrent()) return new(3);
        using var command = ManagementCommand(db, transaction, """
            DELETE FROM guild_applications WHERE guild_id=$guild AND character_id=$id
                AND EXISTS(SELECT 1 FROM characters c WHERE c.id=$id AND c.account=$account)
            """, ("$guild", guildId), ("$id", actor.CharacterId), ("$account", actor.Owner.Account));
        if (command.ExecuteNonQuery() != 1) return new(23);
        long revision = TouchManagement(db, transaction, guildId);
        var guild = ReadApplicationDirectory(db, transaction, guildId);
        transaction.Commit();
        return new(0, guild, Revision: revision);
    });

    public GuildApplicationCommit DecideGuildApplication(GuildManagementIdentity actor, ushort targetId, byte region,
        bool approve, Func<long> now, int memberLimit) => actor.Owner.Execute(() =>
    {
        if (!actor.IsCurrent()) return new GuildApplicationCommit(3);
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.DecideGuildApplication");
        using var transaction = write.Transaction;
        if (!actor.IsCurrent()) return new(3);
        long at = now();
        var state = ReadManagement(db, transaction, actor.Owner.Account, actor.CharacterId);
        if (state is null || state.Grade > 2) return new(36);
        using var command = ManagementCommand(db, transaction, """
            SELECT c.account FROM guild_applications a JOIN characters c ON c.id=a.character_id
            WHERE a.guild_id=$guild AND a.character_id=$id AND a.region=$region
            """, ("$guild", state.Guild.Id), ("$id", targetId), ("$region", region));
        if (region != actor.Channel.ServerIndex || command.ExecuteScalar() is not string account) return new(23);
        command.CommandText = "SELECT name FROM characters WHERE id=$id";
        string targetName = (string)command.ExecuteScalar()!;
        if (approve)
        {
            command.CommandText = "SELECT guild_id FROM guild_members WHERE character_id=$id";
            if (command.ExecuteScalar() is not null) return new(39);
            if (ApplicationCooldown(db, transaction, targetId, at)) return new(103);
            if (state.Members.Length >= GuildMemberLimit(db, transaction, state.Guild.Id, memberLimit)) return new(38);
            InsertApplicationMember(db, transaction, state.Guild.Id, targetId, at);
            RemoveAllGuildApplications(db, transaction, targetId, state.Guild.Id);
        }
        else
        {
            command.CommandText = "DELETE FROM guild_applications WHERE guild_id=$guild AND character_id=$id";
            if (command.ExecuteNonQuery() != 1) return new(23);
        }
        long revision = TouchManagement(db, transaction, state.Guild.Id);
        var changed = ReadManagement(db, transaction, actor.Owner.Account, actor.CharacterId)!;
        var guild = ReadApplicationDirectory(db, transaction, state.Guild.Id)!;
        transaction.Commit();
        return new(0, guild, approve, revision,
            approve ? changed.Members : [new GuildManagementTarget(targetId, account, targetName)]);
    });

    static bool ApplicationCooldown(SqliteConnection db, SqliteTransaction transaction, ushort characterId, long now)
    {
        using var command = ManagementCommand(db, transaction,
            "SELECT 1 FROM guild_membership_cooldowns WHERE character_id=$id AND until_unix>$now",
            ("$id", characterId), ("$now", now));
        return command.ExecuteScalar() is not null;
    }

    static void InsertApplicationMember(SqliteConnection db, SqliteTransaction transaction, uint guildId, ushort id, long now)
    {
        using var command = ManagementCommand(db, transaction, """
            INSERT INTO guild_members(guild_id,character_id,grade,contribution,member_memo,joined_at_unix,last_login_unix)
            VALUES($guild,$id,4,0,'',$now,0)
            """, ("$guild", guildId), ("$id", id), ("$now", now));
        command.ExecuteNonQuery();
    }

    static void RemoveAllGuildApplications(SqliteConnection db, SqliteTransaction transaction, ushort id, uint joinedGuild)
    {
        using var command = ManagementCommand(db, transaction,
            "SELECT guild_id FROM guild_applications WHERE character_id=$id AND guild_id<>$joined",
            ("$id", id), ("$joined", joinedGuild));
        var affected = new List<uint>();
        using (var rows = command.ExecuteReader())
            while (rows.Read()) affected.Add(checked((uint)rows.GetInt64(0)));
        command.CommandText = "DELETE FROM guild_applications WHERE character_id=$id";
        command.ExecuteNonQuery();
        foreach (uint guild in affected) TouchManagement(db, transaction, guild);
    }

    static bool ApplicationListFits(SqliteConnection db, SqliteTransaction transaction, uint guild, string name, string message)
    {
        // 单包完整列表没有续包；按本版wire字节预算限制新增申请，保留所有已接受申请。
        long bytes = 5L + 22 + CharacterName.Encoding.GetByteCount(name) + CharacterName.Encoding.GetByteCount(message);
        using var command = ManagementCommand(db, transaction, """
            SELECT c.name,a.message FROM guild_applications a JOIN characters c ON c.id=a.character_id
            WHERE a.guild_id=$guild AND NOT EXISTS(SELECT 1 FROM guild_members m WHERE m.character_id=c.id)
            """, ("$guild", guild));
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            bytes += 22 + CharacterName.Encoding.GetByteCount(rows.GetString(0)) + CharacterName.Encoding.GetByteCount(rows.GetString(1));
            if (bytes > 65520) return false;
        }
        return bytes <= 65520;
    }
}
