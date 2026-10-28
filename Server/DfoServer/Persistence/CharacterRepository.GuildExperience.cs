using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class GuildExperienceSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS guild_experience_receipts(
            character_id INTEGER NOT NULL,
            event_id TEXT NOT NULL,
            bonuses TEXT NOT NULL,
            PRIMARY KEY(character_id,event_id)
        );
        """;
}

internal sealed partial class CharacterRepository
{
    public GuildExperienceEntitlement? ReadGuildKillEntitlement(ushort characterId, long now)
    {
        using var db = database.Open();
        using var command = ManagementCommand(db, null, """
            SELECT m.guild_id,m.joined_at_unix,b.value,b.expires_unix FROM guild_members m
            JOIN guild_benefits b ON b.guild_id=m.guild_id AND b.type=1
            WHERE m.character_id=$id AND b.value>0 AND b.expires_unix>$now
            """, ("$id", characterId), ("$now", now));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(checked((uint)reader.GetInt64(0)), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3)) : null;
    }

    static ExperienceBonuses? RevalidateGuildExperience(SqliteConnection db, SqliteTransaction tx, string account,
        ushort characterId, ExperienceBonuses? bonuses, long now)
    {
        if (bonuses?.GuildEntitlement is not { } entitlement) return bonuses;
        using var command = ManagementCommand(db, tx, """
            SELECT 1 FROM guild_members m JOIN characters c ON c.id=m.character_id
            JOIN guild_benefits b ON b.guild_id=m.guild_id AND b.type=1
            WHERE m.character_id=$id AND c.account=$account AND m.guild_id=$guild AND m.joined_at_unix=$joined
              AND b.value>=$percent AND b.expires_unix>$now
            """, ("$id", characterId), ("$account", account), ("$guild", entitlement.GuildId),
            ("$joined", entitlement.JoinedAtUnix), ("$percent", entitlement.Percent), ("$now", now));
        return command.ExecuteScalar() is not null ? bonuses : bonuses with
            { Parts = bonuses.Parts.Where(part => part.Source != ExperienceBonusSource.Guild).ToImmutableArray() };
    }

    static ExperienceBonuses? ReadCommittedGuildExperience(SqliteConnection db, SqliteTransaction tx,
        ushort characterId, Guid eventId, ExperienceBonuses? fallback)
    {
        using var command = ManagementCommand(db, tx,
            "SELECT bonuses FROM guild_experience_receipts WHERE character_id=$id AND event_id=$event",
            ("$id", characterId), ("$event", eventId.ToString("N")));
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<ExperienceBonuses>(json)
            ?? throw new InvalidDataException("公会经验回执缺少分项") : fallback;
    }

    static void SaveCommittedGuildExperience(SqliteConnection db, SqliteTransaction tx,
        ushort characterId, Guid eventId, ExperienceBonuses? bonuses)
    {
        if (bonuses?.GuildEntitlement is null) return;
        using var command = ManagementCommand(db, tx, """
            INSERT INTO guild_experience_receipts(character_id,event_id,bonuses) VALUES($id,$event,$bonuses)
            """, ("$id", characterId), ("$event", eventId.ToString("N")), ("$bonuses", JsonSerializer.Serialize(bonuses)));
        command.ExecuteNonQuery();
    }
}
