using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.EpicWish;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IEpicWishStore
{
    static EpicWishList ReadEpicWish(SqliteConnection db, ushort characterId, SqliteTransaction? tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT item_ids FROM character_epic_wishes WHERE character_id=$id";
        query.Parameters.AddWithValue("$id", characterId);
        if (query.ExecuteScalar() is not string json) return EpicWishList.Empty;
        var state = new EpicWishList(JsonSerializer.Deserialize<ImmutableArray<int>>(json));
        state.Validate();
        return state;
    }

    public bool SaveEpicWish(AccountOwners.Lease owner, Character expected, EpicWishList wishes) => owner.Execute(() =>
    {
        wishes.Validate();
        using var db = database.Open();
        using (var read = db.BeginRead())
            if (ValidateSnapshot(db, read)) return false;

        using var write = db.BeginWrite("CharacterRepository.SaveEpicWish");
        using var tx = write.Transaction;
        if (ValidateSnapshot(db, tx)) return false;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO character_epic_wishes(character_id,account,item_ids) VALUES($id,$account,$items)
            ON CONFLICT(character_id) DO UPDATE SET item_ids=excluded.item_ids
            """;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$items", JsonSerializer.Serialize(wishes.Items));
        command.ExecuteNonQuery();
        tx.Commit();
        return true;

        bool ValidateSnapshot(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var query = connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = "SELECT revision FROM characters WHERE id=$id AND account=$account";
            query.Parameters.AddWithValue("$id", expected.Id);
            query.Parameters.AddWithValue("$account", owner.Account);
            if (query.ExecuteScalar() is not long revision || revision != expected.Revision)
                throw new InvalidOperationException("史诗祈愿角色归属或版本已变化");
            var before = ReadEpicWish(connection, expected.Id, transaction);
            if (!before.Same(expected.EpicWishes))
                throw new InvalidOperationException("史诗祈愿保存快照已变化");
            return before.Same(wishes);
        }
    });
}
