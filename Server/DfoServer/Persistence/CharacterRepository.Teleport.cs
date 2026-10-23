using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Town;
using Microsoft.Data.Sqlite;
using System.Collections.Immutable;
using System.Text.Json;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static (byte Type, int Experience, ImmutableHashSet<int> Recipes) ReadExpertJob(SqliteConnection db, ushort id, SqliteTransaction? tx)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT job_type,expertness,learned_recipes FROM character_expert_job WHERE character_id=$id";
        query.Parameters.AddWithValue("$id", id);
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return (0, 0, ImmutableHashSet<int>.Empty);
        int type = reader.GetInt32(0), experience = reader.GetInt32(1);
        if (type is < 1 or > 4 || experience < 0) throw new InvalidDataException("副职业类型超出已确认范围");
        int[] recipes = JsonSerializer.Deserialize<int[]>(reader.GetString(2)) ?? throw new InvalidDataException("副职业配方为空值");
        if (recipes.Length > byte.MaxValue || recipes.Any(id => id <= 0) || recipes.Distinct().Count() != recipes.Length)
            throw new InvalidDataException("副职业已学设计图集合非法");
        return ((byte)type, experience, recipes.ToImmutableHashSet());
    }

    static (int GiveUpState, int Grade, int Endurance) ReadExpertMeta(SqliteConnection db, ushort id, SqliteTransaction? tx)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT giveup_state,machine_grade,machine_endurance FROM character_expert_meta WHERE character_id=$id";
        query.Parameters.AddWithValue("$id", id);
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return (0, 1, 300);
        int giveUp = reader.GetInt32(0), grade = reader.GetInt32(1), endurance = reader.GetInt32(2);
        if (giveUp is < 0 or > 3 || grade is < 0 or > 11 || endurance < 0)
            throw new InvalidDataException("副职业元数据超出已确认范围");
        return (giveUp, grade, endurance);
    }

    static (long Until, int Permille) ReadExpBonus(SqliteConnection db, ushort id, SqliteTransaction? tx)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT until_unix,permille FROM character_exp_bonus WHERE character_id=$id";
        query.Parameters.AddWithValue("$id", id);
        using var reader = query.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(0), reader.GetInt32(1)) : (0, 0);
    }

    static TeleportCooldowns ReadTeleportCooldowns(SqliteConnection db, ushort id, SqliteTransaction? tx)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT potion_until,aura_until,upgrade_aura_until,party_aura_until FROM character_teleport_cooldowns WHERE character_id=$id";
        query.Parameters.AddWithValue("$id", id);
        using var reader = query.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)) : new();
    }

    public Character CommitTeleport(AccountOwners.Lease owner, TeleportPlan plan) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitTeleport");
        using var tx = write.Transaction;
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
            INSERT INTO character_teleport_cooldowns(character_id,account,potion_until,aura_until,upgrade_aura_until,party_aura_until)
            VALUES($id,$account,$potion,$aura,$upgrade,$party)
            ON CONFLICT(character_id) DO UPDATE SET potion_until=excluded.potion_until,aura_until=excluded.aura_until,
                upgrade_aura_until=excluded.upgrade_aura_until,party_aura_until=excluded.party_aura_until;
            """;
        command.Parameters.AddWithValue("$id", current.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$potion", plan.Cooldowns.PotionUntil);
        command.Parameters.AddWithValue("$aura", plan.Cooldowns.AuraUntil);
        command.Parameters.AddWithValue("$upgrade", plan.Cooldowns.UpgradeAuraUntil);
        command.Parameters.AddWithValue("$party", plan.Cooldowns.PartyAuraUntil);
        command.ExecuteNonQuery();
        tx.Commit();
        return current with { Revision = expected.Revision + 1, Position = plan.Position, Return = plan.Return, TeleportCooldowns = plan.Cooldowns };
    });
}
