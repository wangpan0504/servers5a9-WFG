using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static EpicPieceBook ReadEpicPieces(SqliteConnection db, string account, SqliteTransaction? transaction)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision,order_hash,counts FROM account_epic_books WHERE account=$account";
        command.Parameters.AddWithValue("$account", account);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return EpicPieceBook.Empty;
        var book = new EpicPieceBook(reader.GetInt64(0), reader.GetString(1), JsonSerializer.Deserialize<ImmutableArray<int>>(reader.GetString(2)));
        book.Validate();
        return book;
    }

    static InventorySnapshot WriteEpicPieces(SqliteConnection db, SqliteTransaction transaction, AccountOwners.Lease owner,
        ushort characterId, InventorySnapshot before, InventorySnapshot after, string reason)
    {
        var old = before.EpicPieces;
        var book = after.EpicPieces;
        if (old.Same(book))
            return after;
        if (book.Counts.IsEmpty || !old.Counts.IsEmpty && (old.OrderHash != book.OrderHash || old.Counts.Length != book.Counts.Length))
            throw new InvalidOperationException("物品业务不能重排或删除史诗图鉴结构");
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$revision", old.Revision);
        command.Parameters.AddWithValue("$hash", book.OrderHash);
        command.Parameters.AddWithValue("$counts", JsonSerializer.Serialize(book.Counts));
        command.CommandText = old.Counts.IsEmpty ?
            "INSERT INTO account_epic_books(account,revision,order_hash,counts) VALUES($account,1,$hash,$counts)" :
            "UPDATE account_epic_books SET revision=revision+1,counts=$counts WHERE account=$account AND revision=$revision AND order_hash=$hash";
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("账号史诗图鉴版本变化");
        foreach (int index in old.ChangedIndexes(book))
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$id", characterId);
            command.Parameters.AddWithValue("$generation", owner.Generation);
            command.Parameters.AddWithValue("$action", reason);
            command.Parameters.AddWithValue("$hash", book.OrderHash);
            command.Parameters.AddWithValue("$index", index);
            command.Parameters.AddWithValue("$before", old.Get(index));
            command.Parameters.AddWithValue("$after", book.Get(index));
            command.CommandText = """
                INSERT INTO epic_piece_audit_log(account,character_id,generation,action_name,order_hash,piece_index,count_before,count_after)
                VALUES($account,$id,$generation,$action,$hash,$index,$before,$after);
                """;
            command.ExecuteNonQuery();
        }
        return after with { EpicPieces = book with { Revision = checked(old.Revision + 1) } };
    }
}
