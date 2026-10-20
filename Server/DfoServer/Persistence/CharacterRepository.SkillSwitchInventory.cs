using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Skills;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public DfoServer.Game.Characters.Character CommitSkillSwitchInventory(
        AccountOwners.Lease owner,
        SkillSwitchInventoryPlan plan) => owner.Execute(() =>
    {
        plan.Before.SkillSwitchInventory.Validate();
        plan.After.Validate();
        if (!plan.Changed)
            return plan.Before;
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitSkillSwitchInventory");
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, plan.Before.Name);
        if (current.Id != plan.Before.Id || current.Revision != plan.Before.Revision ||
            !current.SkillSwitchInventory.Same(plan.Before.SkillSwitchInventory))
            throw new InvalidOperationException("Buff强化登记角色版本或状态已变化");
        if (plan.ExpectedItem is { } expected &&
            current.Inventory.Find(expected.Position) != expected)
            throw new InvalidOperationException("Buff强化登记物品快照已变化");
        using (var command = db.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = """
                UPDATE characters SET revision=revision+1
                WHERE id=$id AND account=$account AND revision=$revision
                """;
            command.Parameters.AddWithValue("$id", current.Id);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$revision", current.Revision);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Buff强化登记角色版本已变化");
        }
        WriteSkillSwitchInventory(db, tx, current.Id, owner.Account, plan.After);
        tx.Commit();
        return current with
        {
            Revision = current.Revision + 1,
            SkillSwitchInventory = plan.After
        };
    });

    static SkillSwitchInventoryState ReadSkillSwitchInventory(
        SqliteConnection db,
        ushort characterId,
        SqliteTransaction? transaction)
    {
        ushort skillId;
        using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT skill_id FROM character_skill_switch_inventory WHERE character_id=$id";
            command.Parameters.AddWithValue("$id", characterId);
            if (command.ExecuteScalar() is not long value)
                return SkillSwitchInventoryState.Empty;
            skillId = checked((ushort)value);
        }
        var bindings = ImmutableArray.CreateBuilder<SkillSwitchItemBinding>();
        using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT equipment_part,item_uid,item_id
                FROM character_skill_switch_inventory_items
                WHERE character_id=$id ORDER BY equipment_part
                """;
            command.Parameters.AddWithValue("$id", characterId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                bindings.Add(new(checked((byte)reader.GetInt32(0)), reader.GetInt64(1), reader.GetInt32(2)));
        }
        var state = new SkillSwitchInventoryState(skillId, bindings.ToImmutable());
        state.Validate();
        return state;
    }

    static void WriteSkillSwitchInventory(
        SqliteConnection db,
        SqliteTransaction tx,
        ushort characterId,
        string account,
        SkillSwitchInventoryState state)
    {
        state.Validate();
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", characterId);
        command.CommandText = """
            DELETE FROM character_skill_switch_inventory_items WHERE character_id=$id;
            DELETE FROM character_skill_switch_inventory WHERE character_id=$id;
            """;
        command.ExecuteNonQuery();
        if (state.SkillId == 0 && state.Items.Length == 0)
            return;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$skill", state.SkillId);
        command.CommandText = """
            INSERT INTO character_skill_switch_inventory(character_id,account,skill_id)
            VALUES($id,$account,$skill)
            """;
        command.ExecuteNonQuery();
        command.Parameters.AddWithValue("$part", 0);
        command.Parameters.AddWithValue("$uid", 0L);
        command.Parameters.AddWithValue("$item", 0);
        command.CommandText = """
            INSERT INTO character_skill_switch_inventory_items(character_id,equipment_part,item_uid,item_id)
            VALUES($id,$part,$uid,$item)
            """;
        foreach (var binding in state.Items)
        {
            command.Parameters["$part"].Value = binding.EquipmentPart;
            command.Parameters["$uid"].Value = binding.ItemUid;
            command.Parameters["$item"].Value = binding.ItemId;
            command.ExecuteNonQuery();
        }
    }

    static void ReconcileSkillSwitchSkill(SqliteConnection db, SqliteTransaction tx, ushort characterId)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            DELETE FROM character_skill_switch_inventory
            WHERE character_id=$id AND skill_id<>0 AND NOT EXISTS(
                SELECT 1 FROM character_skills
                WHERE character_id=$id AND skill_id=character_skill_switch_inventory.skill_id AND level>0
            )
            """;
        command.Parameters.AddWithValue("$id", characterId);
        command.ExecuteNonQuery();
    }
}
