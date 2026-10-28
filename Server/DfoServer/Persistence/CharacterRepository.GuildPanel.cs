using DfoServer.Game.Accounts;
using DfoServer.Game.Guild;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public GuildPanel? ReadGuildPanel(AccountOwners.Lease owner, ushort characterId) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var transaction = db.BeginRead();
        using var query = db.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT g.guild_id,g.master_character_id
            FROM guild_members m JOIN guilds g ON g.guild_id=m.guild_id
            JOIN characters c ON c.id=m.character_id WHERE c.id=$character AND c.account=$account
            """;
        query.Parameters.AddWithValue("$character", characterId);
        query.Parameters.AddWithValue("$account", owner.Account);
        long guildId;
        ushort masterId;
        using (var row = query.ExecuteReader())
        {
            if (!row.Read()) return null;
            guildId = row.GetInt64(0);
            masterId = checked((ushort)row.GetInt32(1));
        }
        var membership = ReadGuildMembership(db, characterId, transaction);
        if (membership is null) return null;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        query.CommandText = """
            SELECT c.id,c.account,c.name,c.level,c.job,c.grow_type,m.grade,m.member_memo,
                m.contribution,m.last_login_unix,
                COALESCE((SELECT SUM(e.points) FROM guild_contribution_events e WHERE e.guild_id=m.guild_id
                  AND e.character_id=m.character_id AND e.joined_at_unix=m.joined_at_unix
                  AND e.week_start_unix=$week AND e.created_at_unix<=$now),0)
            FROM guild_members m JOIN characters c ON c.id=m.character_id
            WHERE m.guild_id=$guild ORDER BY c.id
            """;
        query.Parameters.Clear();
        query.Parameters.AddWithValue("$guild", guildId);
        query.Parameters.AddWithValue("$week", GuildContributionRules.WeekStart(now));
        query.Parameters.AddWithValue("$now", now);
        var members = new List<GuildPanelMember>();
        using (var row = query.ExecuteReader())
        {
            while (row.Read())
            {
                ushort id = checked((ushort)row.GetInt32(0));
                byte storedGrade = checked((byte)row.GetInt32(6));
                // 建会旧数据的双方都是0；仅投影纠正，不能把所有0号成员解释成会长。
                byte grade = id == masterId ? (byte)1 : storedGrade is >= 2 and <= 5 ? storedGrade : (byte)4;
                members.Add(new(id, row.GetString(1), row.GetString(2), checked((ushort)row.GetInt32(3)),
                    checked((byte)row.GetInt32(4)), checked((byte)row.GetInt32(5)), grade, row.GetString(7),
                    checked((uint)row.GetInt64(8)), row.GetInt64(9))
                    { WeeklyContribution = checked((uint)row.GetInt64(10)) });
            }
        }
        return new GuildPanel(membership, masterId, members.ToArray())
        {
            Management = ReadManagement(db, transaction, owner.Account, characterId),
            Economy = ReadEconomy(db, transaction, owner.Account, characterId, now),
            DungeonProgress = ReadGuildDungeonProgress(db, transaction, checked((uint)guildId)),
            HongbaoSpentCera = ReadGuildHongbaoSpent(db, transaction, characterId, GuildAttendanceDay.Start(now))
        };
    });
}
