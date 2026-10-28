using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Guild;
using DfoServer.GameWorld.Guilds;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public GuildDungeonAdmission? PrepareGuildDungeonEntry(AccountOwners.Lease owner, ushort characterId,
        uint dungeonId, GuildDungeonCatalog catalog) => owner.Execute<GuildDungeonAdmission?>(() =>
    {
        if (catalog.Find(dungeonId) is null) return null;
        using var db = database.Open(); using var tx = db.BeginRead();
        using var query = ManagementCommand(db, tx, "SELECT 1 FROM characters WHERE id=$id AND account=$account",
            ("$id", characterId), ("$account", owner.Account));
        if (query.ExecuteScalar() is null) return null;
        var membership = ReadDungeonGuildMembership(db, tx, characterId);
        if (membership.GuildId == 0) return null;
        var progress = ReadGuildDungeonProgress(db, tx, membership.GuildId);
        return catalog.IsUnlocked(dungeonId, EffectiveGuildDungeonCounts(progress, catalog))
            ? new(dungeonId, membership, catalog) : null;
    });

    static GuildDungeonProgress ReadGuildDungeonProgress(SqliteConnection db, SqliteTransaction? tx, uint guildId)
    {
        using var query = ManagementCommand(db, tx, """
            SELECT p.dungeon_id,p.clear_count,p.last_clear_unix,e.effective_clear_units,e.decay_anchor_unix
            FROM guild_dungeon_progress p LEFT JOIN guild_dungeon_effective_progress e USING(guild_id,dungeon_id)
            WHERE p.guild_id=$guild ORDER BY p.dungeon_id
            """, ("$guild", guildId));
        var counts = System.Collections.Immutable.ImmutableArray.CreateBuilder<GuildDungeonClearCount>();
        long lastClear = 0;
        using var reader = query.ExecuteReader();
        while (reader.Read())
        {
            counts.Add(new(checked((uint)reader.GetInt64(0)), checked((uint)reader.GetInt64(1)),
                reader.IsDBNull(3) ? null : checked((uint)reader.GetInt64(3)), reader.IsDBNull(4) ? 0 : reader.GetInt64(4)));
            lastClear = Math.Max(lastClear, reader.GetInt64(2));
        }
        return new(guildId, counts.ToImmutable(), lastClear);
    }

    static Dictionary<uint, uint> EffectiveGuildDungeonCounts(GuildDungeonProgress progress, GuildDungeonCatalog catalog) =>
        progress.Counts.ToDictionary(row => row.DungeonId, row => row.Effective(catalog, DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Units);

    static void AdmitGuildDungeon(SqliteConnection db, SqliteTransaction tx, ushort characterId, DungeonEntryCharge charge)
    {
        if (charge.GuildDungeon is not { } admission)
        {
            if (charge.DungeonId is 8500 or 8503 or 8504 or 8505 or 8507 or 8508)
                throw new InvalidOperationException("公会副本缺少已冻结的准入资格");
            return;
        }
        if (charge.DungeonId != admission.DungeonId || admission.Membership.CharacterId != characterId ||
            admission.Catalog.Find(admission.DungeonId) is null ||
            ReadDungeonGuildMembership(db, tx, characterId) != admission.Membership)
            throw new GuildDungeonAdmissionRejectedException("公会副本入场会籍已变化");
        var progress = ReadGuildDungeonProgress(db, tx, admission.Membership.GuildId);
        if (!admission.Catalog.IsUnlocked(admission.DungeonId, EffectiveGuildDungeonCounts(progress, admission.Catalog)))
            throw new GuildDungeonAdmissionRejectedException("公会副本前置净化不足");
    }

    GuildDungeonProgress? CommitGuildDungeonProgress(SqliteConnection db, SqliteTransaction tx, PreparedDungeonSettlement prepared, long now)
    {
        if (prepared.GuildDungeonCatalog?.Find(prepared.Fact.DungeonId) is null) return null;
        var frozen = prepared.GuildMemberships.SingleOrDefault(row => row.CharacterId == prepared.Fact.Identity.CharacterId);
        if (frozen is null || frozen.GuildId == 0 || ReadDungeonGuildMembership(db, tx, frozen.CharacterId) != frozen) return null;
        string instance = prepared.Fact.Identity.InstanceId.ToString("N");
        using var command = ManagementCommand(db, tx, """
            SELECT dungeon_id FROM guild_dungeon_clear_receipts WHERE guild_id=$guild AND instance_id=$instance
            """, ("$guild", frozen.GuildId), ("$instance", instance));
        if (command.ExecuteScalar() is long existing)
        {
            if (existing != prepared.Fact.DungeonId) throw new InvalidDataException("公会副本同实例通关图被修改");
            return ReadGuildDungeonProgress(db, tx, frozen.GuildId);
        }
        var catalog = prepared.GuildDungeonCatalog!;
        var definition = catalog.Find(prepared.Fact.DungeonId)!;
        var previous = ReadGuildDungeonProgress(db, tx, frozen.GuildId).Counts.FirstOrDefault(row => row.DungeonId == definition.Id)
            ?? new GuildDungeonClearCount(definition.Id, 0);
        uint units = previous.Effective(catalog, now).Units;
        uint limit = checked((uint)(((long)definition.GaugeLimit + definition.GaugeOnClear - 1) / definition.GaugeOnClear));
        command.Parameters.AddWithValue("$dungeon", prepared.Fact.DungeonId);
        command.Parameters.AddWithValue("$now", now);
        // 本服同一真实实例每公会只累计一次；成员各自的贡献和资产仍走各自原有结算。
        command.CommandText = """
            INSERT INTO guild_dungeon_progress(guild_id,dungeon_id,clear_count,last_clear_unix) VALUES($guild,$dungeon,1,$now)
            ON CONFLICT(guild_id,dungeon_id) DO UPDATE SET clear_count=MIN(clear_count+1,4294967295),last_clear_unix=$now
            """;
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO guild_dungeon_effective_progress(guild_id,dungeon_id,effective_clear_units,decay_anchor_unix)
            VALUES($guild,$dungeon,$units,$now)
            ON CONFLICT(guild_id,dungeon_id) DO UPDATE SET effective_clear_units=$units,decay_anchor_unix=MAX(decay_anchor_unix,$now)
            """;
        command.Parameters.AddWithValue("$units", Math.Min(units + 1, limit));
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO guild_dungeon_clear_receipts(guild_id,instance_id,dungeon_id,character_id,joined_at_unix,committed_at_unix)
            VALUES($guild,$instance,$dungeon,$id,$joined,$now)
            """;
        command.Parameters.AddWithValue("$id", frozen.CharacterId);
        command.Parameters.AddWithValue("$joined", frozen.JoinedAtUnix);
        command.ExecuteNonQuery();
        return ReadGuildDungeonProgress(db, tx, frozen.GuildId);
    }

    static GuildDungeonProgress? RestoreGuildDungeonProgress(SqliteConnection db, SqliteTransaction tx, PreparedDungeonSettlement prepared)
    {
        if (prepared.GuildDungeonCatalog?.Find(prepared.Fact.DungeonId) is null) return null;
        var frozen = prepared.GuildMemberships.SingleOrDefault(row => row.CharacterId == prepared.Fact.Identity.CharacterId);
        if (frozen is null || frozen.GuildId == 0 || ReadDungeonGuildMembership(db, tx, frozen.CharacterId) != frozen) return null;
        using var query = ManagementCommand(db, tx, """
            SELECT guild_id FROM guild_dungeon_clear_receipts WHERE instance_id=$instance AND dungeon_id=$dungeon
              AND guild_id=$guild
            """, ("$instance", prepared.Fact.Identity.InstanceId.ToString("N")),
            ("$dungeon", prepared.Fact.DungeonId), ("$guild", frozen.GuildId));
        return query.ExecuteScalar() is long guild ? ReadGuildDungeonProgress(db, tx, checked((uint)guild)) : null;
    }
}
