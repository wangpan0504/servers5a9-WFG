using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Town;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static TeleportCooldowns ReadTeleportCooldowns(SqliteConnection db, ushort id, SqliteTransaction? tx)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT potion_until,aura_until FROM character_teleport_cooldowns WHERE character_id=$id";
        query.Parameters.AddWithValue("$id", id);
        using var reader = query.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), reader.GetInt64(1)) : new();
    }

    public Character CommitTeleport(AccountOwners.Lease owner, TeleportPlan plan) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        var expected = plan.Expected;
        // 提交器返回同一事务内实读角色；不在其前后重复加载完整资产。
        var current = CommitInventoryInTransaction(db, tx, owner, expected, plan.Inventory);
        if (current.Position != expected.Position || current.Return != expected.Return || current.TeleportCooldowns != expected.TeleportCooldowns ||
            !current.CompletedQuests.SequenceEqual(expected.CompletedQuests) || !current.AccountCompletedQuests.SequenceEqual(expected.AccountCompletedQuests))
            throw new InvalidOperationException("传送来源、权限或冷却已变化");
        // 同一事务复用完整背包提交器；任何位置/冷却写入失败都会回滚扣药和审计。
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            UPDATE characters SET revision=$nextRevision,town=$town,area=$area,x=$x,y=$y,direction=$direction,
              return_town=$rtown,return_area=$rarea,return_x=$rx,return_y=$ry,return_direction=$rdirection
            WHERE id=$id AND account=$account AND revision=$revision;
            """;
        command.Parameters.AddWithValue("$id", current.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$revision", current.Revision);
        command.Parameters.AddWithValue("$nextRevision", expected.Revision + 1);
        Bind(command, "", plan.Position); Bind(command, "r", plan.Return);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("传送角色版本已变化");
        command.Parameters.Clear();
        command.CommandText = """
            INSERT INTO character_teleport_cooldowns(character_id,account,potion_until,aura_until)
            VALUES($id,$account,$potion,$aura)
            ON CONFLICT(character_id) DO UPDATE SET potion_until=excluded.potion_until,aura_until=excluded.aura_until;
            """;
        command.Parameters.AddWithValue("$id", current.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$potion", plan.Cooldowns.PotionUntil);
        command.Parameters.AddWithValue("$aura", plan.Cooldowns.AuraUntil);
        command.ExecuteNonQuery();
        tx.Commit();
        return current with { Revision = expected.Revision + 1, Position = plan.Position, Return = plan.Return, TeleportCooldowns = plan.Cooldowns };
    });
}
