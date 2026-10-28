using DfoServer.Game.Guild;
using DfoServer.GameWorld.Guilds;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class GuildGrowthItemSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS guild_growth_item_receipts(
          request_id TEXT PRIMARY KEY CHECK(length(request_id)=64),
          account TEXT NOT NULL,character_id INTEGER NOT NULL,
          guild_id INTEGER NOT NULL CHECK(guild_id>0),joined_at_unix INTEGER NOT NULL CHECK(joined_at_unix>=0),
          source_uid INTEGER NOT NULL CHECK(source_uid>0),source_slot INTEGER NOT NULL CHECK(source_slot>=0),
          item_id INTEGER NOT NULL CHECK(item_id>0),amount INTEGER NOT NULL CHECK(amount>0),
          before_experience INTEGER NOT NULL CHECK(before_experience BETWEEN 0 AND 4294967295),
          after_experience INTEGER NOT NULL CHECK(after_experience BETWEEN 0 AND 4294967295),
          before_level INTEGER NOT NULL CHECK(before_level BETWEEN 1 AND 10),
          after_level INTEGER NOT NULL CHECK(after_level BETWEEN 1 AND 10),
          created_at_unix INTEGER NOT NULL CHECK(created_at_unix BETWEEN 1 AND 2147483647),
          CHECK(after_experience=before_experience+amount)
        );
        """;
}

internal sealed partial class CharacterRepository : IGuildExperienceStore
{
    static GuildExperienceMembership? ReadGrowthMembership(SqliteConnection db, SqliteTransaction? tx,
        GuildManagementIdentity actor)
    {
        using var command = ManagementCommand(db, tx, """
            SELECT m.guild_id,m.joined_at_unix FROM guild_members m JOIN characters c ON c.id=m.character_id
            WHERE c.id=$id AND c.account=$account
            """, ("$id", actor.CharacterId), ("$account", actor.Owner.Account));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(checked((uint)reader.GetInt64(0)), reader.GetInt64(1)) : null;
    }
    public GuildExperienceMembership? ReadGuildExperienceMembership(GuildManagementIdentity actor) => actor.Owner.Execute(() =>
    {
        if (!actor.IsCurrent()) return null;
        using var db = database.Open(); return ReadGrowthMembership(db, null, actor);
    });
    static bool GrowthReceipt(SqliteConnection db, SqliteTransaction tx, GuildManagementIdentity actor, string key,
        GuildExperienceMembership membership, short slot, GuildExperienceGiftPlan? plan, out bool matches)
    {
        using var command = ManagementCommand(db, tx, """
            SELECT account,character_id,guild_id,joined_at_unix,source_uid,source_slot,item_id,amount
            FROM guild_growth_item_receipts WHERE request_id=$key
            """, ("$key", key));
        using var reader = command.ExecuteReader(); matches = false;
        if (!reader.Read()) return false;
        matches = reader.GetString(0) == actor.Owner.Account && reader.GetInt32(1) == actor.CharacterId &&
            reader.GetInt64(2) == membership.GuildId && reader.GetInt64(3) == membership.JoinedAtUnix && reader.GetInt32(5) == slot &&
            (plan is null || reader.GetInt64(4) == plan.Source.Uid && reader.GetInt32(6) == plan.Definition.Item.Id &&
                reader.GetInt32(7) == plan.Definition.Amount);
        return true;
    }
    public GuildExperienceCommit? ReplayGuildExperienceGift(GuildManagementIdentity actor, string requestId, short slot, long now) =>
        actor.Owner.Execute<GuildExperienceCommit?>(() =>
        {
            if (!actor.IsCurrent() || !GuildEconomyService.ValidRequestId(requestId) || now is <= 0 or > int.MaxValue) return new(19);
            using var db = database.Open(); using var tx = db.BeginRead();
            if (!GuildRequestAvailable(db, tx, requestId, 5, actor.Owner.Account, actor.CharacterId)) return new(14);
            if (ReadGrowthMembership(db, tx, actor) is not { } membership) return new(34);
            if (!GrowthReceipt(db, tx, actor, requestId, membership, slot, null, out bool matches)) return null;
            return matches ? new(0, ReadManagement(db, tx, actor.Owner.Account, actor.CharacterId), true) : new(14);
        });
    public GuildExperienceCommit CommitGuildExperienceGift(GuildManagementIdentity actor, GuildExperienceGiftPlan plan,
        GuildParameterCatalog parameters, string requestId, long now) => actor.Owner.Execute<GuildExperienceCommit>(() =>
    {
        if (!actor.IsCurrent() || !GuildEconomyService.ValidRequestId(requestId) || now is <= 0 or > int.MaxValue ||
            plan.Expected.Id != actor.CharacterId || plan.Source.Uid <= 0 || plan.Source.IsExpired(now) ||
            plan.Expected.Inventory.Find(plan.Source.Position) != plan.Source || plan.Source.Core.ItemId != plan.Definition.Item.Id ||
            plan.Definition.Amount <= 0 || !ReferenceEquals(plan.Expected.Inventory, plan.Inventory.Before) ||
            plan.Inventory.Reason != "guild_experience_gift") return new(19);
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.CommitGuildExperienceGift");
        using var tx = write.Transaction;
        if (!GuildRequestAvailable(db, tx, requestId, 5, actor.Owner.Account, actor.CharacterId)) return new(14);
        var state = ReadManagement(db, tx, actor.Owner.Account, actor.CharacterId);
        var membership = ReadGrowthMembership(db, tx, actor);
        if (state is null || membership is null) return new(34);
        if (membership != plan.Membership) return new(14);
        if (GrowthReceipt(db, tx, actor, requestId, membership, plan.Source.Position.Index, plan, out bool matches))
            return matches ? new(0, state, true) : new(14);
        long experience = state.Guild.Experience, after = experience + plan.Definition.Amount;
        if (after > uint.MaxValue) return new(22);
        byte level = parameters.LevelAtExperience(state.Guild.Tier, after);
        var current = Require(db, tx, actor.Owner.Account, actor.Name);
        if (current.Position != plan.Expected.Position || !plan.Definition.Item.AllowsUse(current.Level, current.Job)) return new(19);
        var committed = CommitInventoryInTransaction(db, tx, actor.Owner, plan.Expected, plan.Inventory);
        using var command = ManagementCommand(db, tx, """
            UPDATE guilds SET experience=$after,level=$level
            WHERE guild_id=$guild AND experience=$before AND tier=$tier AND level=$oldLevel
            """, ("$after", after), ("$level", level), ("$guild", state.Guild.Id), ("$before", experience),
            ("$tier", state.Guild.Tier), ("$oldLevel", state.Guild.Level));
        if (command.ExecuteNonQuery() != 1) return new(14);
        command.CommandText = """
            INSERT INTO guild_growth_item_receipts(request_id,account,character_id,guild_id,joined_at_unix,
                source_uid,source_slot,item_id,amount,before_experience,after_experience,before_level,after_level,created_at_unix)
            VALUES($key,$account,$id,$guild,$joined,$uid,$slot,$item,$amount,$before,$after,$oldLevel,$level,$now)
            """;
        command.Parameters.AddWithValue("$key", requestId); command.Parameters.AddWithValue("$account", actor.Owner.Account);
        command.Parameters.AddWithValue("$id", actor.CharacterId); command.Parameters.AddWithValue("$joined", membership.JoinedAtUnix);
        command.Parameters.AddWithValue("$uid", plan.Source.Uid); command.Parameters.AddWithValue("$slot", plan.Source.Position.Index);
        command.Parameters.AddWithValue("$item", plan.Definition.Item.Id); command.Parameters.AddWithValue("$amount", plan.Definition.Amount);
        command.Parameters.AddWithValue("$now", now); command.ExecuteNonQuery();
        ClaimGuildRequest(db, tx, requestId, 5, actor.Owner.Account, actor.CharacterId);
        TouchManagement(db, tx, state.Guild.Id);
        var result = ReadManagement(db, tx, actor.Owner.Account, actor.CharacterId)
            ?? throw new InvalidDataException("公会经验书提交后会籍消失");
        if (!actor.IsCurrent()) return new(3);
        tx.Commit(); return new(0, result, Character: committed with { Guild = result.Guild }, Inventory: plan.Inventory);
    });
}
