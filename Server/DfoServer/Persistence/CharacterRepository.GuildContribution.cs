using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IGuildContributionStore
{
    public GuildContributionCommit ObserveGuildContribution(GuildManagementIdentity actor, GuildContributionSource source, long now) =>
        actor.Owner.Execute(() =>
        {
            if (!actor.IsCurrent() || now is <= 0 or > int.MaxValue) return new GuildContributionCommit(3);
            if (source is not (GuildContributionSource.Login or GuildContributionSource.Chat)) return new(23);
            using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.ObserveGuildContribution");
            using var tx = write.Transaction;
            string eventId = $"daily:{actor.CharacterId}:{GuildAttendanceDay.Start(now)}:{(byte)source}";
            var result = CreditGuildContributionInTransaction(db, tx, actor.Owner, actor.CharacterId, source, eventId, now);
            if (!actor.IsCurrent()) return new(3);
            tx.Commit(); return result;
        });

    // 结算调用方与资产共用一个事务；回执先记录零归属，防止事后入会重放旧事件。
    internal GuildContributionCommit CreditGuildContributionInTransaction(SqliteConnection db, SqliteTransaction tx,
        AccountOwners.Lease owner, ushort id, GuildContributionSource source, string eventId, long now,
        int? awardedPoints = null, GuildDungeonMembership? expectedMembership = null)
        => CreditGuildContributionInTransaction(db, tx, owner.Account, id, source, eventId, now, awardedPoints, expectedMembership);

    // 团本终局只持原生命周期事务，不在这里重新取得在线账号或嵌套写事务。
    GuildContributionCommit CreditGuildContributionInTransaction(SqliteConnection db, SqliteTransaction tx,
        string account, ushort id, GuildContributionSource source, string eventId, long now,
        int? awardedPoints, GuildDungeonMembership? expectedMembership)
    {
        if (eventId.Length is < 1 or > 256 || now is <= 0 or > int.MaxValue)
            throw new InvalidDataException("公会贡献事件无效");
        using (var ownership = ManagementCommand(db, tx, "SELECT 1 FROM characters WHERE id=$id AND account=$account",
            ("$id", id), ("$account", account)))
            if (ownership.ExecuteScalar() is null) return new(3);
        int amount = awardedPoints ?? GuildContributionRules.Points(source);
        if (amount <= 0 || expectedMembership is { } expected && expected.CharacterId != id)
            throw new InvalidDataException("公会贡献冻结归属或分值无效");
        long day = GuildAttendanceDay.Start(now), week = GuildContributionRules.WeekStart(now);
        using var command = ManagementCommand(db, tx, """
            SELECT character_id,source FROM guild_contribution_events
            WHERE event_id=$event OR (character_id=$id AND business_day_unix=$day AND source=$source AND source IN (1,2))
            """, ("$event", eventId), ("$id", id), ("$day", day), ("$source", (byte)source));
        using (var row = command.ExecuteReader())
            if (row.Read())
            {
                if (row.GetInt32(0) != id || row.GetInt32(1) != (byte)source)
                    throw new InvalidDataException("公会贡献事件归属不符");
                return new(0);
            }
        var state = ReadManagement(db, tx, account, id);
        // 尚未入会的登录不是公会登录；加入公会后当天首次真实重登仍可领取。
        if (state is null && source is GuildContributionSource.Login or GuildContributionSource.Chat)
            return new(0);
        long joined = 0;
        if (state is not null)
        {
            command.CommandText = "SELECT joined_at_unix FROM guild_members WHERE character_id=$id AND guild_id=$guild";
            command.Parameters.AddWithValue("$guild", state.Guild.Id);
            joined = Convert.ToInt64(command.ExecuteScalar());
            if (joined > now) return new(3);
            if (expectedMembership is { } frozen && (frozen.GuildId != state.Guild.Id || frozen.JoinedAtUnix != joined))
                state = null;
        }
        if (state is not null)
        {
            command.CommandText = """
                UPDATE guild_members SET contribution=contribution+$amount
                WHERE character_id=$id AND guild_id=$guild AND joined_at_unix=$joined AND contribution<=$ceiling
                """;
            command.Parameters.AddWithValue("$amount", amount); command.Parameters.AddWithValue("$joined", joined);
            command.Parameters.AddWithValue("$ceiling", uint.MaxValue - (long)amount);
            if (command.ExecuteNonQuery() != 1) return new(22);
        }
        command.CommandText = """
            INSERT INTO guild_contribution_events(event_id,character_id,guild_id,joined_at_unix,source,
                business_day_unix,week_start_unix,points,created_at_unix)
            SELECT $event,$id,$guildId,$joinedAt,$source,$day,$week,$points,$now
            WHERE EXISTS(SELECT 1 FROM characters WHERE id=$id AND account=$account)
            """;
        command.Parameters.AddWithValue("$guildId", state is null ? DBNull.Value : state.Guild.Id);
        command.Parameters.AddWithValue("$joinedAt", joined); command.Parameters.AddWithValue("$week", week);
        command.Parameters.AddWithValue("$points", state is null ? 0 : amount); command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$account", account);
        if (command.ExecuteNonQuery() != 1) return new(3);
        if (state is null) return new(0);
        long revision = TouchManagement(db, tx, state.Guild.Id);
        // 登录、聊天、大厅等待及正式通关只沿此真实正积分writer推进，保持同一业务事务。
        ApplyBattlePassGuildContribution(db, tx, account, amount, now);
        return new(0, state.Guild.Id, revision, amount, Members: state.Members);
    }

    public GuildContributionCommit SettleGuildContribution(GuildManagementIdentity actor, Character expected, long now,
        Func<Character, int, InventoryPlan?> prepare) => actor.Owner.Execute(() =>
    {
        if (!actor.IsCurrent() || now is <= 0 or > int.MaxValue) return new GuildContributionCommit(3);
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.SettleGuildContribution");
        using var tx = write.Transaction;
        var state = ReadManagement(db, tx, actor.Owner.Account, actor.CharacterId);
        if (state is null) return new(34);
        using var command = ManagementCommand(db, tx,
            "SELECT joined_at_unix FROM guild_members WHERE character_id=$id AND guild_id=$guild",
            ("$id", actor.CharacterId), ("$guild", state.Guild.Id));
        long joined = Convert.ToInt64(command.ExecuteScalar());
        var weeks = ReadPendingContributionWeeks(db, tx, state.Guild.Id, actor.CharacterId, joined, now);
        if (weeks.Length == 0) return new(0);
        int amount = checked(weeks.Sum(week => GuildContributionRules.Coins(week.Points)));
        var current = Require(db, tx, actor.Owner.Account, actor.Name);
        if (current.Id != expected.Id || current.Id != actor.CharacterId || current.Revision != expected.Revision ||
            current.Position != expected.Position) return new(3);
        long held = current.Inventory.Items.Where(item => item.Position.List == InventoryListType.Main &&
            item.Core.ItemId == GuildContributionRules.CoinItemId).Sum(item => (long)item.Core.Count);
        // 上限不足保留整笔待结算贡献；不静默吞币，不先记成功回执再尝试发奖。
        if (held + amount > GuildContributionRules.CoinLimit(state.Guild.Tier)) return new(22);
        var plan = prepare(expected, amount);
        if (plan is null) return new(13);
        if (!ReferenceEquals(plan.Before, expected.Inventory) || plan.Reason != "guild_weekly_coins" || plan.OverflowItems.Length != 0)
            throw new InvalidDataException("公会周币资产规划无效");
        var committed = CommitInventoryInTransaction(db, tx, actor.Owner, expected, plan);
        command.CommandText = """
            INSERT INTO guild_contribution_settlements(guild_id,character_id,joined_at_unix,week_start_unix,points,coins,settled_at_unix)
            VALUES($guild,$id,$joined,$week,$points,$coins,$now)
            """;
        command.Parameters.AddWithValue("$joined", joined); command.Parameters.AddWithValue("$week", 0L);
        command.Parameters.AddWithValue("$points", 0); command.Parameters.AddWithValue("$coins", 0);
        command.Parameters.AddWithValue("$now", now);
        foreach (var week in weeks)
        {
            command.Parameters["$week"].Value = week.StartUnix; command.Parameters["$points"].Value = week.Points;
            command.Parameters["$coins"].Value = GuildContributionRules.Coins(week.Points); command.ExecuteNonQuery();
        }
        if (!actor.IsCurrent()) return new(3);
        tx.Commit();
        return new(0, state.Guild.Id, state.Revision, Coins: amount, Character: committed, Inventory: plan, Members: state.Members);
    });

    static GuildContributionWeek[] ReadPendingContributionWeeks(SqliteConnection db, SqliteTransaction tx,
        uint guildId, ushort id, long joined, long now)
    {
        using var command = ManagementCommand(db, tx, """
            SELECT e.week_start_unix,SUM(e.points) FROM guild_contribution_events e
            WHERE e.guild_id=$guild AND e.character_id=$id AND e.joined_at_unix=$joined AND e.week_start_unix<$week
              AND NOT EXISTS(SELECT 1 FROM guild_contribution_settlements s WHERE s.guild_id=e.guild_id
                AND s.character_id=e.character_id AND s.joined_at_unix=e.joined_at_unix AND s.week_start_unix=e.week_start_unix)
            GROUP BY e.week_start_unix HAVING SUM(e.points)>0 ORDER BY e.week_start_unix
            """, ("$guild", guildId), ("$id", id), ("$joined", joined), ("$week", GuildContributionRules.WeekStart(now)));
        var weeks = new List<GuildContributionWeek>();
        using var row = command.ExecuteReader();
        while (row.Read()) weeks.Add(new(row.GetInt64(0), checked((int)row.GetInt64(1))));
        return weeks.ToArray();
    }
}
