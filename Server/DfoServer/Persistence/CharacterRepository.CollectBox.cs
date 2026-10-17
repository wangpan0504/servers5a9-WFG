using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.CollectBox;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static CollectBoxState ReadCollectBox(SqliteConnection db, ushort characterId, SqliteTransaction? transaction)
    {
        byte selected = 0;
        using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT selected_box FROM character_collectbox_state WHERE character_id=$id";
            command.Parameters.AddWithValue("$id", characterId);
            if (command.ExecuteScalar() is long value)
                selected = checked((byte)value);
        }

        var items = ImmutableArray.CreateBuilder<CollectedBoxItem>();
        using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT box_index,slot_index,item_id FROM character_collectbox_slots
                WHERE character_id=$id ORDER BY box_index,slot_index
                """;
            command.Parameters.AddWithValue("$id", characterId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                items.Add(new(checked((byte)reader.GetInt32(0)), checked((ushort)reader.GetInt32(1)), reader.GetInt32(2)));
        }

        var expirations = ImmutableArray.CreateBuilder<CollectBoxExpiration>();
        using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT box_index,expires_at FROM character_collectbox_expirations
                WHERE character_id=$id ORDER BY box_index
                """;
            command.Parameters.AddWithValue("$id", characterId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                expirations.Add(new(checked((byte)reader.GetInt32(0)), reader.GetInt64(1)));
        }
        var state = new CollectBoxState(selected, items.ToImmutable(), expirations.ToImmutable());
        state.Validate();
        return state;
    }

    public Character CommitCollectBox(AccountOwners.Lease owner, CollectBoxPlan plan) => owner.Execute(() =>
    {
        ValidateInventoryPlan(plan.Before, plan.Inventory);
        plan.After.Validate();
        if (!plan.Changed)
            return plan.Before;

        using var db = database.Open();
        using var transaction = db.BeginTransaction(deferred: false);
        var expected = plan.Before;
        var current = Require(db, transaction, owner.Account, expected.Name);
        if (current.Id != expected.Id || current.Revision != expected.Revision ||
            !current.CollectBox.Same(expected.CollectBox))
            throw new InvalidOperationException("宝珠收集箱角色版本或状态已变化");

        if (plan.Inventory.Changed)
            current = CommitInventoryInTransaction(db, transaction, owner, current, plan.Inventory);
        else
        {
            using var revision = db.CreateCommand();
            revision.Transaction = transaction;
            revision.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$revision";
            revision.Parameters.AddWithValue("$id", expected.Id);
            revision.Parameters.AddWithValue("$account", owner.Account);
            revision.Parameters.AddWithValue("$revision", expected.Revision);
            if (revision.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("宝珠收集箱角色版本失配");
            current = current with { Revision = current.Revision + 1 };
        }

        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", current.Id);
        command.CommandText = """
            DELETE FROM character_collectbox_slots WHERE character_id=$id;
            DELETE FROM character_collectbox_expirations WHERE character_id=$id;
            DELETE FROM character_collectbox_state WHERE character_id=$id;
            """;
        command.ExecuteNonQuery();

        command.Parameters.Clear();
        command.CommandText = """
            INSERT INTO character_collectbox_state(character_id,account,selected_box)
            VALUES($id,$account,$selected);
            """;
        command.Parameters.AddWithValue("$id", current.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$selected", plan.After.SelectedBox);
        command.ExecuteNonQuery();

        command.CommandText = """
            INSERT INTO character_collectbox_slots(character_id,account,box_index,slot_index,item_id)
            VALUES($id,$account,$box,$slot,$item);
            """;
        foreach (var item in plan.After.Items)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", current.Id);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$box", item.Box);
            command.Parameters.AddWithValue("$slot", item.PhysicalSlot);
            command.Parameters.AddWithValue("$item", item.ItemId);
            command.ExecuteNonQuery();
        }

        command.CommandText = """
            INSERT INTO character_collectbox_expirations(character_id,account,box_index,expires_at)
            VALUES($id,$account,$box,$expires);
            """;
        foreach (var expiration in plan.After.Expirations)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", current.Id);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$box", expiration.Box);
            command.Parameters.AddWithValue("$expires", expiration.ExpiresAt);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return current with { CollectBox = plan.After };
    });
}
