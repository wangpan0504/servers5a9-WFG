using DfoServer.Game.Guild;
using DfoServer.GameWorld.Guilds;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IGuildHallWaitStore
{
    public GuildDungeonMembership? ReadGuildHallWaitMembership(GuildManagementIdentity actor) => actor.Owner.Execute(() =>
    {
        if (!actor.IsCurrent()) return null;
        using var db = database.Open();
        return ReadHallWaitMembership(db, null, actor);
    });

    static GuildDungeonMembership? ReadHallWaitMembership(SqliteConnection db, SqliteTransaction? tx, GuildManagementIdentity actor)
    {
        using var command = ManagementCommand(db, tx, """
            SELECT m.guild_id,m.joined_at_unix FROM guild_members m JOIN characters c ON c.id=m.character_id
            WHERE m.character_id=$id AND c.account=$account
            """, ("$id", actor.CharacterId), ("$account", actor.Owner.Account));
        using var row = command.ExecuteReader();
        return row.Read() ? new(actor.CharacterId, checked((uint)row.GetInt64(0)), row.GetInt64(1)) : null;
    }

    public GuildHallWaitCommit CreditGuildHallWait(GuildManagementIdentity actor, GuildHallWaitCredit credit,
        GuildHallWaitCatalog rules, long now) => actor.Owner.Execute(() =>
    {
        if (!actor.IsCurrent()) return new GuildHallWaitCommit(new(3), 0, false);
        if (now is <= 0 or > int.MaxValue || credit.Membership.CharacterId != actor.CharacterId ||
            credit.BusinessDay != GuildAttendanceDay.Start(now) || credit.Periods <= 0 ||
            credit.Periods > rules.DailyMaximum / (long)rules.Points + 1 ||
            !credit.EventId.StartsWith($"hall:{actor.CharacterId}:", StringComparison.Ordinal) || credit.EventId.Length > 256)
            throw new InvalidDataException("公会大厅等待贡献事件无效");
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.CreditGuildHallWait");
        using var tx = write.Transaction;
        var membership = ReadHallWaitMembership(db, tx, actor);
        if (membership is null || membership != credit.Membership || membership.JoinedAtUnix > now) return new(new(0), 0, false);
        using var command = ManagementCommand(db, tx, """
            SELECT COALESCE(SUM(points),0) FROM guild_contribution_events
            WHERE character_id=$id AND business_day_unix=$day AND source=7
            """, ("$id", actor.CharacterId), ("$day", credit.BusinessDay));
        int earned = checked((int)Convert.ToInt64(command.ExecuteScalar()));
        command.CommandText = "SELECT character_id,source,business_day_unix,joined_at_unix,guild_id FROM guild_contribution_events WHERE event_id=$event";
        command.Parameters.AddWithValue("$event", credit.EventId);
        using (var row = command.ExecuteReader())
            if (row.Read())
            {
                if (row.GetInt32(0) != actor.CharacterId || row.GetInt32(1) != 7 ||
                    row.GetInt64(2) != credit.BusinessDay || row.GetInt64(3) != membership.JoinedAtUnix ||
                    row.IsDBNull(4) || row.GetInt64(4) != membership.GuildId)
                    throw new InvalidDataException("公会大厅等待贡献回执归属不符");
                return new(new(0), earned);
            }
        int amount = (int)Math.Min(Math.Max(0, rules.DailyMaximum - (long)earned), (long)credit.Periods * rules.Points);
        if (amount == 0) return new(new(0), earned);
        var result = CreditGuildContributionInTransaction(db, tx, actor.Owner, actor.CharacterId,
            GuildContributionSource.HallWait, credit.EventId, now, amount, membership);
        if (!result.Success || !actor.IsCurrent()) return new(new(3), earned);
        tx.Commit();
        return new(result, checked(earned + amount));
    });
}
