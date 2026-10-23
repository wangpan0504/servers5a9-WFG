using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static readonly string[] MakingColumns = ["weapon", "cloth", "leather", "light_armor", "heavy_armor", "plate", "amulet", "wrist", "ring", "support", "magic_stone"];

    static ItemMakingSkillState ReadItemMakingSkills(SqliteConnection db, SqliteTransaction? tx, ushort characterId, string account)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT account," + string.Join(',', MakingColumns) + " FROM item_making_skill_info WHERE charac_no=$id";
        command.Parameters.AddWithValue("$id", characterId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return ItemMakingSkillState.Empty;
        if (reader.GetString(0) != account) throw new InvalidDataException("制作熟练度账号归属不符");
        var result = new ItemMakingSkillState(Enumerable.Range(1, ItemMakingSkillState.Count)
            .Select(index => checked((ushort)reader.GetInt32(index))).ToImmutableArray());
        result.Validate();
        return result;
    }

    static void WriteItemMakingSkills(SqliteConnection db, SqliteTransaction tx, ushort characterId, string account, InventoryPlan plan)
    {
        if (plan.Before.ItemMakingSkills.Same(plan.After.ItemMakingSkills)) return;
        plan.After.ItemMakingSkills.Validate();
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO item_making_skill_info(charac_no,account," + string.Join(',', MakingColumns) + ") VALUES($id,$account," +
            string.Join(',', Enumerable.Range(0, MakingColumns.Length).Select(index => "$p" + index)) + ") ON CONFLICT(charac_no) DO UPDATE SET " +
            string.Join(',', MakingColumns.Select(name => name + "=excluded." + name)) + " WHERE account=$account";
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$account", account);
        for (int index = 0; index < MakingColumns.Length; index++)
            command.Parameters.AddWithValue("$p" + index, plan.After.ItemMakingSkills.Points[index]);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("制作熟练度提交归属变化");
    }
}
