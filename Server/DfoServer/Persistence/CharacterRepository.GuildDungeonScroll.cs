using DfoServer.Game.Guild;
using DfoServer.GameWorld.Guilds;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class GuildDungeonScrollSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS guild_dungeon_scroll_receipts(
          request_id TEXT PRIMARY KEY CHECK(length(request_id)=64),
          account TEXT NOT NULL,character_id INTEGER NOT NULL,
          guild_id INTEGER NOT NULL CHECK(guild_id>0),joined_at_unix INTEGER NOT NULL CHECK(joined_at_unix>=0),
          source_uid INTEGER NOT NULL CHECK(source_uid>0),source_slot INTEGER NOT NULL CHECK(source_slot>=0),
          item_id INTEGER NOT NULL CHECK(item_id>0),
          dungeon_id INTEGER NOT NULL CHECK(dungeon_id BETWEEN 1 AND 4294967295),
          increment INTEGER NOT NULL CHECK(increment>0),
          before_clears INTEGER NOT NULL CHECK(before_clears BETWEEN 0 AND 4294967295),
          after_clears INTEGER NOT NULL CHECK(after_clears BETWEEN 0 AND 4294967295),
          before_units INTEGER NOT NULL CHECK(before_units BETWEEN 0 AND 4294967295),
          after_units INTEGER NOT NULL CHECK(after_units BETWEEN 0 AND 4294967295),
          created_at_unix INTEGER NOT NULL CHECK(created_at_unix BETWEEN 1 AND 2147483647),
          CHECK(after_clears>=before_clears),
          CHECK(after_units>=before_units)
        );
        """;
}

internal sealed partial class CharacterRepository : IGuildDungeonScrollStore
{
    public GuildDungeonMembership? ReadGuildDungeonScrollMembership(GuildManagementIdentity actor) => actor.Owner.Execute(() =>
    {
        if (!actor.IsCurrent()) return null;
        using var db = database.Open(); using var tx = db.BeginRead();
        return ReadDungeonGuildMembership(db, tx, actor.CharacterId) is { GuildId: > 0 } membership ? membership : null;
    });

    static bool ScrollReceipt(SqliteConnection db, SqliteTransaction tx, GuildManagementIdentity actor, string key,
        GuildDungeonMembership membership, short slot, GuildDungeonScrollPlan? plan, out bool matches)
    {
        using var command = ManagementCommand(db, tx, """
            SELECT account,character_id,guild_id,joined_at_unix,source_uid,source_slot,item_id,dungeon_id,increment
            FROM guild_dungeon_scroll_receipts WHERE request_id=$key
            """, ("$key", key));
        using var reader = command.ExecuteReader(); matches = false;
        if (!reader.Read()) return false;
        matches = reader.GetString(0) == actor.Owner.Account && reader.GetInt32(1) == actor.CharacterId &&
            reader.GetInt64(2) == membership.GuildId && reader.GetInt64(3) == membership.JoinedAtUnix && reader.GetInt32(5) == slot &&
            (plan is null || reader.GetInt64(4) == plan.Source.Uid && reader.GetInt32(6) == plan.Definition.Item.Id &&
                reader.GetInt64(7) == plan.Definition.DungeonId && reader.GetInt32(8) == plan.Definition.Increment);
        return true;
    }

    public GuildDungeonScrollCommit? ReplayGuildDungeonScroll(GuildManagementIdentity actor, string requestId, short slot, long now) =>
        actor.Owner.Execute<GuildDungeonScrollCommit?>(() =>
        {
            if (!actor.IsCurrent() || !GuildEconomyService.ValidRequestId(requestId) || now is <= 0 or > int.MaxValue) return new(19);
            using var db = database.Open(); using var tx = db.BeginRead();
            if (!GuildRequestAvailable(db, tx, requestId, 7, actor.Owner.Account, actor.CharacterId)) return new(14);
            if (ReadDungeonGuildMembership(db, tx, actor.CharacterId) is not { GuildId: > 0 } membership) return new(34);
            if (!ScrollReceipt(db, tx, actor, requestId, membership, slot, null, out bool matches)) return null;
            return matches ? new(0, ReadManagement(db, tx, actor.Owner.Account, actor.CharacterId),
                ReadGuildDungeonProgress(db, tx, membership.GuildId), true) : new(14);
        });

    public GuildDungeonScrollCommit CommitGuildDungeonScroll(GuildManagementIdentity actor, GuildDungeonScrollPlan plan,
        GuildDungeonCatalog catalog, string requestId, long now) => actor.Owner.Execute<GuildDungeonScrollCommit>(() =>
    {
        if (!actor.IsCurrent() || !GuildEconomyService.ValidRequestId(requestId) || now is <= 0 or > int.MaxValue ||
            plan.Expected.Id != actor.CharacterId || plan.Source.Uid <= 0 || plan.Source.IsExpired(now) ||
            plan.Expected.Inventory.Find(plan.Source.Position) != plan.Source || plan.Source.Core.ItemId != plan.Definition.Item.Id ||
            plan.Definition.Increment <= 0 || plan.Definition.DungeonId == 0 ||
            !ReferenceEquals(plan.Expected.Inventory, plan.Inventory.Before) ||
            plan.Inventory.Reason != "guild_dungeon_scroll" || catalog.Find(plan.Definition.DungeonId) is not { } dungeon)
            return new(19);
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.CommitGuildDungeonScroll");
        using var tx = write.Transaction;
        if (!GuildRequestAvailable(db, tx, requestId, 7, actor.Owner.Account, actor.CharacterId)) return new(14);
        var state = ReadManagement(db, tx, actor.Owner.Account, actor.CharacterId);
        var membership = ReadDungeonGuildMembership(db, tx, actor.CharacterId);
        if (state is null || membership.GuildId == 0) return new(34);
        if (membership != plan.Membership) return new(14);
        if (ScrollReceipt(db, tx, actor, requestId, membership, plan.Source.Position.Index, plan, out bool matches))
            return matches ? new(0, state, ReadGuildDungeonProgress(db, tx, membership.GuildId), true) : new(14);
        var previous = ReadGuildDungeonProgress(db, tx, membership.GuildId).Counts
            .FirstOrDefault(row => row.DungeonId == dungeon.Id) ?? new GuildDungeonClearCount(dungeon.Id, 0);
        uint units = previous.Effective(catalog, now).Units;
        uint limit = checked((uint)(((long)dungeon.GaugeLimit + dungeon.GaugeOnClear - 1) / dungeon.GaugeOnClear));
        if (units >= limit) return new(22);
        uint increment = checked((uint)plan.Definition.Increment);
        uint afterUnits = units + Math.Min(increment, limit - units);
        uint afterClears = previous.Clears > uint.MaxValue - increment ? uint.MaxValue : previous.Clears + increment;
        var current = Require(db, tx, actor.Owner.Account, actor.Name);
        if (current.Position != plan.Expected.Position || !plan.Definition.Item.AllowsUse(current.Level, current.Job)) return new(19);
        var committed = CommitInventoryInTransaction(db, tx, actor.Owner, plan.Expected, plan.Inventory);
        using var command = ManagementCommand(db, tx, """
            INSERT INTO guild_dungeon_progress(guild_id,dungeon_id,clear_count,last_clear_unix)
            VALUES($guild,$dungeon,$afterClears,$now)
            ON CONFLICT(guild_id,dungeon_id) DO UPDATE SET clear_count=$afterClears,last_clear_unix=$now
            """, ("$guild", membership.GuildId), ("$dungeon", dungeon.Id), ("$afterClears", afterClears), ("$now", now));
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO guild_dungeon_effective_progress(guild_id,dungeon_id,effective_clear_units,decay_anchor_unix)
            VALUES($guild,$dungeon,$units,$now)
            ON CONFLICT(guild_id,dungeon_id) DO UPDATE SET effective_clear_units=$units,decay_anchor_unix=MAX(decay_anchor_unix,$now)
            """;
        command.Parameters.AddWithValue("$units", afterUnits);
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO guild_dungeon_scroll_receipts(request_id,account,character_id,guild_id,joined_at_unix,
                source_uid,source_slot,item_id,dungeon_id,increment,before_clears,after_clears,before_units,after_units,created_at_unix)
            VALUES($key,$account,$id,$guild,$joined,$uid,$slot,$item,$dungeon,$increment,$beforeClears,$afterClears,$beforeUnits,$units,$now)
            """;
        command.Parameters.AddWithValue("$key", requestId); command.Parameters.AddWithValue("$account", actor.Owner.Account);
        command.Parameters.AddWithValue("$id", actor.CharacterId); command.Parameters.AddWithValue("$joined", membership.JoinedAtUnix);
        command.Parameters.AddWithValue("$uid", plan.Source.Uid); command.Parameters.AddWithValue("$slot", plan.Source.Position.Index);
        command.Parameters.AddWithValue("$item", plan.Definition.Item.Id); command.Parameters.AddWithValue("$increment", plan.Definition.Increment);
        command.Parameters.AddWithValue("$beforeClears", previous.Clears); command.Parameters.AddWithValue("$beforeUnits", units);
        command.ExecuteNonQuery();
        ClaimGuildRequest(db, tx, requestId, 7, actor.Owner.Account, actor.CharacterId);
        TouchManagement(db, tx, membership.GuildId);
        var result = ReadManagement(db, tx, actor.Owner.Account, actor.CharacterId)
            ?? throw new InvalidDataException("公会净化卷轴提交后会籍消失");
        var progress = ReadGuildDungeonProgress(db, tx, membership.GuildId);
        if (!actor.IsCurrent()) return new(3);
        tx.Commit(); return new(0, result, progress, Character: committed with { Guild = result.Guild }, Inventory: plan.Inventory);
    });
}
