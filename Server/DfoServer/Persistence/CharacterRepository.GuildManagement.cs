using DfoServer.Game.Accounts;
using DfoServer.Game.Guild;
using DfoServer.GameWorld.Channels;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IGuildManagementStore
{
    public GuildManagementState? ReadGuildManagement(AccountOwners.Lease owner, ushort characterId) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var transaction = db.BeginRead();
        return ReadManagement(db, transaction, owner.Account, characterId);
    });

    public GuildManagementTarget? FindGuildManagementTarget(string name)
    {
        using var db = database.Open();
        using var command = ManagementCommand(db, null, "SELECT id,account,name FROM characters WHERE name=$name", ("$name", name));
        using var row = command.ExecuteReader();
        return row.Read() ? new(checked((ushort)row.GetInt32(0)), row.GetString(1), row.GetString(2)) : null;
    }

    static SqliteCommand ManagementCommand(SqliteConnection db, SqliteTransaction? transaction, string sql,
        params (string Name, object Value)[] values)
    {
        var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        return command;
    }

    static GuildManagementState? ReadManagement(SqliteConnection db, SqliteTransaction? transaction, string account, ushort characterId)
    {
        using var command = ManagementCommand(db, transaction, """
            SELECT g.guild_id,g.master_character_id,m.grade,COALESCE(s.revision,0),COALESCE(s.promotion,''),
                COALESCE(s.tags,0),COALESCE(s.join_mode,0),COALESCE(s.webpage,0),r.region,r.channel
            FROM guild_members m JOIN guilds g ON g.guild_id=m.guild_id
            JOIN characters c ON c.id=m.character_id LEFT JOIN guild_settings s ON s.guild_id=g.guild_id
            LEFT JOIN guild_recommended_channels r ON r.guild_id=g.guild_id
            WHERE c.id=$id AND c.account=$account
            """, ("$id", characterId), ("$account", account));
        uint guildId;
        ushort master;
        byte grade;
        long revision;
        string promotion;
        uint tags;
        byte joinMode;
        byte webpage;
        ChannelIdentity? recommended;
        using (var row = command.ExecuteReader())
        {
            if (!row.Read()) return null;
            guildId = checked((uint)row.GetInt64(0));
            master = checked((ushort)row.GetInt32(1));
            grade = GuildRoleRules.NormalizeGrade(characterId, master, checked((byte)row.GetInt32(2)));
            revision = row.GetInt64(3);
            promotion = row.GetString(4);
            tags = checked((uint)row.GetInt64(5));
            joinMode = checked((byte)row.GetInt32(6));
            webpage = checked((byte)row.GetInt32(7));
            recommended = row.IsDBNull(8) ? null : new(checked((byte)row.GetInt32(8)), checked((byte)row.GetInt32(9)));
        }
        var guild = ReadGuildMembership(db, characterId, transaction);
        if (guild is null) return null;
        var roles = Enumerable.Range(0, 6).Select(value => new GuildRole((byte)value,
            GuildRoleRules.DefaultPermissions((byte)value), GuildRoleRules.DefaultName((byte)value))).ToArray();
        command.CommandText = "SELECT grade,permissions,name FROM guild_roles WHERE guild_id=$guild";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$guild", guildId);
        using (var row = command.ExecuteReader())
            while (row.Read())
            {
                byte index = checked((byte)row.GetInt32(0));
                roles[index] = new(index, checked((uint)row.GetInt64(1)), row.GetString(2));
            }
        command.CommandText = """
            SELECT c.id,c.account,c.name FROM guild_members m JOIN characters c ON c.id=m.character_id
            WHERE m.guild_id=$guild ORDER BY c.id
            """;
        var members = new List<GuildManagementTarget>();
        using (var row = command.ExecuteReader())
            while (row.Read()) members.Add(new(checked((ushort)row.GetInt32(0)), row.GetString(1), row.GetString(2)));
        return new(guild, master, grade, roles[grade].Permissions, revision, promotion, tags, joinMode, roles, members.ToArray())
            { Webpage = webpage, RecommendedChannel = recommended };
    }

    static long TouchManagement(SqliteConnection db, SqliteTransaction transaction, uint guildId)
    {
        using var command = ManagementCommand(db, transaction, """
            INSERT INTO guild_settings(guild_id,revision) VALUES($guild,1)
            ON CONFLICT(guild_id) DO UPDATE SET revision=revision+1;
            SELECT revision FROM guild_settings WHERE guild_id=$guild
            """, ("$guild", guildId));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static int GuildMemberLimit(SqliteConnection db, SqliteTransaction tx, uint guildId, int fallback)
    {
        using var command = ManagementCommand(db, tx,
            "SELECT COALESCE((SELECT member_limit FROM guild_economy WHERE guild_id=$guild),$fallback)",
            ("$guild", guildId), ("$fallback", fallback));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public GuildManagementCommit ChangeGuildManagement(GuildManagementIdentity actor, GuildManagementChange change, long now) =>
        actor.Owner.Execute(() =>
        {
            if (!actor.IsCurrent()) return new GuildManagementCommit(3);
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.ChangeGuildManagement");
            using var transaction = write.Transaction;
            var state = ReadManagement(db, transaction, actor.Owner.Account, actor.CharacterId);
            if (state is null) return new(34);
            uint guildId = state.Guild.Id;
            var target = change.Target ?? new(actor.CharacterId, actor.Owner.Account, actor.Name);
            var actual = state.Members.FirstOrDefault(value => value.Id == target.Id && value.Account == target.Account && value.Name == target.Name);
            using var command = ManagementCommand(db, transaction, "SELECT 1");
            GuildManagementTarget? removed = null;
            bool voluntary = false;
            bool disband = false;
            bool changed = true;
            if (change.Action is GuildManagementAction.Notice or GuildManagementAction.Memo)
            {
                bool notice = change.Action == GuildManagementAction.Notice;
                if (!GuildManagementRequestsValidText(change.Text, notice)) return new(108);
                if (notice && (state.Permissions & (1u << 4)) == 0) return new(36);
                command.CommandText = notice ? "UPDATE guilds SET notice=$text WHERE guild_id=$guild AND notice<>$text" :
                    "UPDATE guild_members SET member_memo=$text WHERE guild_id=$guild AND character_id=$id AND member_memo<>$text";
                command.Parameters.AddWithValue("$guild", guildId);
                command.Parameters.AddWithValue("$id", actor.CharacterId);
                command.Parameters.AddWithValue("$text", change.Text);
                changed = command.ExecuteNonQuery() != 0;
            }
            else
            {
                if (actual is null) return new(34);
                voluntary = actual.Id == actor.CharacterId;
                disband = change.Action == GuildManagementAction.Disband || voluntary && actor.CharacterId == state.MasterId;
                if (disband)
                {
                    if (actor.CharacterId != state.MasterId || state.Grade != 1) return new(36);
                    if (state.Members.Length != 1) return new(87);
                    using var cargo = ManagementCommand(db, transaction,
                        "SELECT 1 FROM guild_cargo_slots WHERE guild_id=$guild LIMIT 1", ("$guild", guildId));
                    if (cargo.ExecuteScalar() is not null) return new(87);
                }
                else if (!voluntary)
                {
                    var targetState = ReadManagement(db, transaction, actual.Account, actual.Id);
                    if (targetState is null || targetState.Guild.Id != guildId) return new(34);
                    if (actual.Id == state.MasterId || targetState.Grade == 1) return new(87);
                    if (!GuildRoleRules.CanKick(state.Grade, targetState.Grade, state.Permissions)) return new(36);
                }
                removed = actual;
                command.CommandText = "DELETE FROM guild_members WHERE guild_id=$guild AND character_id=$id";
                command.Parameters.AddWithValue("$guild", guildId);
                command.Parameters.AddWithValue("$id", actual.Id);
                if (command.ExecuteNonQuery() != 1) return new(34);
                // 入会时间只有秒级精度；被踢后同秒重入也不能继承旧个人福利。
                command.CommandText = "DELETE FROM character_guild_npc_benefits WHERE guild_id=$guild AND character_id=$id";
                command.ExecuteNonQuery();
                if (voluntary)
                {
                    command.CommandText = """
                        INSERT INTO guild_membership_cooldowns(character_id,until_unix) VALUES($id,$until)
                        ON CONFLICT(character_id) DO UPDATE SET until_unix=excluded.until_unix
                        """;
                    command.Parameters.AddWithValue("$until", GuildRoleRules.DepartureDeadline(now));
                    command.ExecuteNonQuery();
                }
            }
            long revision = changed ? TouchManagement(db, transaction, guildId) : state.Revision;
            if (disband)
            {
                command.CommandText = "DELETE FROM guilds WHERE guild_id=$guild";
                command.ExecuteNonQuery();
            }
            if (!actor.IsCurrent()) return new(3);
            transaction.Commit();
            return new(0, guildId, revision, removed, state.Guild.Name, voluntary,
                state.Members.Where(value => value.Id != removed?.Id).ToArray());
        });

    static bool GuildManagementRequestsValidText(string text, bool notice) =>
        Game.Characters.CharacterName.Encoding.GetByteCount(text) <= (notice ? 255 : 20) &&
        !text.Any(value => char.IsControl(value) && !(notice && value is '\r' or '\n' or '\t'));

    public GuildManagementCommit JoinGuildInvitation(GuildManagementIdentity inviter, GuildManagementIdentity target,
        uint expectedGuild, long deadline, int capacity, Func<long> now) => AccountOwners.Lease.ExecuteTogether(inviter.Owner, target.Owner, () =>
    {
        if (!inviter.IsCurrent() || !target.IsCurrent() || inviter.Channel != target.Channel) return new GuildManagementCommit(3);
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.JoinGuildInvitation");
        using var transaction = write.Transaction;
        long instant = now() / 1000;
        if (now() >= deadline) return new(103);
        var state = ReadManagement(db, transaction, inviter.Owner.Account, inviter.CharacterId);
        if (state is null || state.Guild.Id != expectedGuild) return new(34);
        if ((state.Permissions & 2) == 0) return new(36);
        if (state.Members.Length >= GuildMemberLimit(db, transaction, expectedGuild, capacity)) return new(38);
        using var command = ManagementCommand(db, transaction, """
            SELECT c.account,c.name,EXISTS(SELECT 1 FROM guild_members m WHERE m.character_id=c.id),
                COALESCE((SELECT until_unix FROM guild_membership_cooldowns WHERE character_id=c.id),0)
            FROM characters c WHERE c.id=$id
            """, ("$id", target.CharacterId));
        using (var row = command.ExecuteReader())
        {
            if (!row.Read() || row.GetString(0) != target.Owner.Account || row.GetString(1) != target.Name) return new(3);
            if (row.GetInt32(2) != 0) return new(39);
            if (row.GetInt64(3) > instant) return new(23);
        }
        command.CommandText = """
            INSERT INTO guild_members(guild_id,character_id,grade,contribution,member_memo,joined_at_unix,last_login_unix)
            VALUES($guild,$id,4,0,'',$now,$now)
            """;
        command.Parameters.AddWithValue("$guild", expectedGuild);
        command.Parameters.AddWithValue("$now", instant);
        command.ExecuteNonQuery();
        RemoveAllGuildApplications(db, transaction, target.CharacterId, expectedGuild);
        long revision = TouchManagement(db, transaction, expectedGuild);
        if (!inviter.IsCurrent() || !target.IsCurrent() || now() >= deadline) return new(103);
        transaction.Commit();
        return new(0, expectedGuild, revision, Members: [.. state.Members, new(target.CharacterId, target.Owner.Account, target.Name)]);
    });
}
