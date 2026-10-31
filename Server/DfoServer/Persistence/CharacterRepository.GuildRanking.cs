using System.Text.Json;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Guild;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IGuildRankingStore
{
    public uint ReadOfficialGuildRank(uint guild, int month)
    {
        using var db = database.Open();
        using var query = ManagementCommand(db, null, """
            SELECT json_extract(e.value,'$.Rank') FROM guild_ranking_months r,json_each(r.entries) e
            WHERE r.month=$month AND r.rule_version=$rule AND json_extract(e.value,'$.Id')=$guild
            """, ("$month", month), ("$rule", GuildRankingService.RuleVersion), ("$guild", guild));
        return query.ExecuteScalar() is long rank ? checked((uint)rank) : 0;
    }

    public GuildRankingData ReadGuildRanking(long now)
    {
        using var db = database.Open();
        using var tx = db.BeginRead();
        var identities = new List<(uint Id, string Name, string Master, uint Mark, byte Mode)>();
        using (var query = ManagementCommand(db, tx, """
            SELECT g.guild_id,g.name,c.name,COALESCE(a.mark,0),COALESCE(s.join_mode,0)
            FROM guilds g JOIN characters c ON c.id=g.master_character_id
            LEFT JOIN guild_appearance a ON a.guild_id=g.guild_id LEFT JOIN guild_settings s ON s.guild_id=g.guild_id
            ORDER BY g.guild_id
            """))
        using (var row = query.ExecuteReader())
            while (row.Read()) identities.Add((checked((uint)row.GetInt64(0)), row.GetString(1), row.GetString(2),
                checked((uint)row.GetInt64(3)), checked((byte)row.GetInt32(4))));
        var members = new Dictionary<uint, List<GuildRankingMember>>();
        using (var query = ManagementCommand(db, tx, """
            SELECT m.guild_id,c.id,c.account,COALESCE(a.level,0),COALESCE(json_extract(p.state_json,'$.Experience'),-1)
            FROM guild_members m JOIN characters c ON c.id=m.character_id
            LEFT JOIN account_adventures a ON a.account=c.account
            LEFT JOIN character_pvp_ratings p ON p.character_id=c.id AND p.account=c.account ORDER BY c.id
            """))
        using (var row = query.ExecuteReader())
            while (row.Read()) Add(members, checked((uint)row.GetInt64(0)), new(checked((ushort)row.GetInt32(1)),
                row.GetString(2), row.GetInt32(3), row.GetInt32(4)));
        var activity = new Dictionary<uint, (int Uv, long Points)>();
        using (var query = ManagementCommand(db, tx, """
            SELECT e.guild_id,COUNT(DISTINCT CASE WHEN e.source=1 AND e.week_start_unix=$week THEN e.character_id END),
              SUM(CASE WHEN e.created_at_unix>=$month THEN e.points ELSE 0 END)
            FROM guild_contribution_events e JOIN guild_members m ON m.guild_id=e.guild_id
              AND m.character_id=e.character_id AND m.joined_at_unix=e.joined_at_unix
            WHERE e.created_at_unix<=$now AND (e.created_at_unix>=$month OR e.week_start_unix=$week) GROUP BY e.guild_id
            """, ("$week", GuildContributionRules.WeekStart(now)), ("$month", GuildRankingService.MonthStart(now)), ("$now", now)))
        using (var row = query.ExecuteReader())
            while (row.Read()) activity.Add(checked((uint)row.GetInt64(0)), (row.GetInt32(1), row.GetInt64(2)));
        var raids = new Dictionary<uint, int>();
        using (var query = ManagementCommand(db, tx, """
            SELECT e.guild_id,COUNT(*) FROM guild_contribution_events e
            JOIN guild_members m ON m.character_id=e.character_id AND m.guild_id=e.guild_id
                AND m.joined_at_unix=e.joined_at_unix
            WHERE e.source=5 AND e.points>0 AND e.created_at_unix BETWEEN $month AND $now
            GROUP BY e.guild_id
            """, ("$month", GuildRankingService.MonthStart(now)), ("$now", now)))
        using (var row = query.ExecuteReader())
            while (row.Read()) raids.Add(checked((uint)row.GetInt64(0)), row.GetInt32(1));
        var dungeons = new Dictionary<uint, List<GuildDungeonClearCount>>();
        using (var query = ManagementCommand(db, tx, """
            SELECT p.guild_id,p.dungeon_id,p.clear_count,e.effective_clear_units,e.decay_anchor_unix
            FROM guild_dungeon_progress p LEFT JOIN guild_dungeon_effective_progress e USING(guild_id,dungeon_id)
            """))
        using (var row = query.ExecuteReader())
            while (row.Read()) Add(dungeons, checked((uint)row.GetInt64(0)), new(checked((uint)row.GetInt64(1)), checked((uint)row.GetInt64(2)),
                row.IsDBNull(3) ? null : checked((uint)row.GetInt64(3)), row.IsDBNull(4) ? 0 : row.GetInt64(4)));
        var parties = new Dictionary<uint, List<GuildRankingParty>>();
        using (var query = ManagementCommand(db, tx, """
            SELECT guild_id,dungeon_id,MAX(members),COUNT(*) FROM guild_ranking_party_clears
            WHERE created_at BETWEEN $month AND $now GROUP BY guild_id,dungeon_id
            """, ("$month", GuildRankingService.MonthStart(now)), ("$now", now)))
        using (var row = query.ExecuteReader())
            while (row.Read()) Add(parties, checked((uint)row.GetInt64(0)), new(checked((uint)row.GetInt64(1)), row.GetInt32(2), row.GetInt64(3)));
        // 福利按当前实际持有的内容类别计分；组队地图种类不代表购买了公会福利。
        var contents = new Dictionary<uint, int>();
        using (var query = ManagementCommand(db, tx, """
            SELECT guild_id,COUNT(*) FROM (
              SELECT guild_id,type FROM guild_benefits WHERE value>0 AND (type=7 OR expires_unix=0 OR expires_unix>$now)
              UNION ALL SELECT guild_id,4 FROM guild_cargo_state WHERE capacity>8
              UNION ALL SELECT guild_id,6 FROM guild_economy WHERE member_limit>300
            ) GROUP BY guild_id
            """, ("$now", now)))
        using (var row = query.ExecuteReader())
            while (row.Read()) contents.Add(checked((uint)row.GetInt64(0)), row.GetInt32(1));
        int previous = GuildRankingService.PreviousMonth(GuildRankingService.Month(now));
        var archives = new List<GuildRankingArchive>();
        using (var query = ManagementCommand(db, tx, """
            SELECT month,observed_at,entries FROM guild_ranking_months WHERE month IN($first,$second) AND rule_version=$rule
            """, ("$first", previous), ("$second", GuildRankingService.PreviousMonth(previous)), ("$rule", GuildRankingService.RuleVersion)))
        using (var row = query.ExecuteReader())
            while (row.Read()) archives.Add(new(row.GetInt32(0), row.GetInt64(1),
                JsonSerializer.Deserialize<GuildRankingEntry[]>(row.GetString(2)) ?? throw new InvalidDataException("公会历史榜缺少条目")));
        return new(identities.Select(g => new GuildRankingFact(g.Id, g.Name, g.Master, g.Mark, g.Mode,
            members.GetValueOrDefault(g.Id)?.ToArray() ?? [], activity.GetValueOrDefault(g.Id).Uv,
            activity.GetValueOrDefault(g.Id).Points, raids.GetValueOrDefault(g.Id),
            dungeons.GetValueOrDefault(g.Id)?.ToArray() ?? [], parties.GetValueOrDefault(g.Id)?.ToArray() ?? [],
            contents.GetValueOrDefault(g.Id))).ToArray(), archives.ToArray());
    }

    static void Add<T>(Dictionary<uint, List<T>> rows, uint guild, T value)
    {
        if (!rows.TryGetValue(guild, out var list)) rows.Add(guild, list = []);
        list.Add(value);
    }

    public void ObserveGuildRanking(GuildRankingArchive archive)
    {
        if (archive.Month != GuildRankingService.Month(archive.ObservedAt)) throw new InvalidDataException("公会历史榜月份与采样时刻不符");
        string entries = JsonSerializer.Serialize(archive.Entries);
        using var db = database.Open();
        using var write = db.BeginWrite("GuildRanking.Observe");
        using var query = ManagementCommand(db, write.Transaction, """
            INSERT INTO guild_ranking_months(month,observed_at,rule_version,entries) VALUES($month,$time,$rule,$entries)
            ON CONFLICT(month) DO UPDATE SET observed_at=$time,rule_version=$rule,entries=$entries WHERE observed_at<$time
            """, ("$month", archive.Month), ("$time", archive.ObservedAt), ("$rule", GuildRankingService.RuleVersion), ("$entries", entries));
        query.ExecuteNonQuery();
        write.Transaction.Commit();
    }

    static void CommitGuildRankingParty(SqliteConnection db, SqliteTransaction tx, PreparedDungeonSettlement prepared)
    {
        if (prepared.GuildMemberships.IsDefaultOrEmpty) return;
        // 仅记录本次实际结算者所在公会；不能代替尚未成功结算的另一公会记账。
        var own = prepared.GuildMemberships.SingleOrDefault(m => m.CharacterId == prepared.Fact.Identity.CharacterId);
        if (own is null || own.GuildId == 0 || ReadDungeonGuildMembership(db, tx, own.CharacterId) != own) return;
        int count = prepared.GuildMemberships.Where(m => m.GuildId == own.GuildId &&
            ReadDungeonGuildMembership(db, tx, m.CharacterId) == m).Select(m => m.CharacterId).Distinct().Count();
        if (count < 2) return;
        using var query = ManagementCommand(db, tx, """
            INSERT INTO guild_ranking_party_clears(guild_id,instance_id,dungeon_id,members,created_at)
            VALUES($guild,$instance,$dungeon,$members,$now) ON CONFLICT(guild_id,instance_id) DO NOTHING
            """, ("$guild", own.GuildId), ("$instance", prepared.Fact.Identity.InstanceId.ToString("N")),
            ("$dungeon", prepared.Fact.DungeonId), ("$members", count), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        query.ExecuteNonQuery();
    }
}
