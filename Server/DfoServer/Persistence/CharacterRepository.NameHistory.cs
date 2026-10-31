using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : ICharacterNameHistoryStore
{
    public ImmutableArray<CharacterFormerName> ReadNameHistory(AccountOwners.Lease owner, ushort characterId) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT former_name,changed_at FROM character_name_history
            WHERE character_id=$id AND account=$account ORDER BY revision DESC LIMIT 255
            """;
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$account", owner.Account);
        using var reader = command.ExecuteReader();
        var rows = ImmutableArray.CreateBuilder<CharacterFormerName>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), checked((uint)reader.GetInt64(1))));
        return rows.ToImmutable();
    });

    static void RecordFormerName(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner, Character before, string newName)
    {
        if (before.Name == newName) return;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO character_name_history(character_id,account,revision,former_name,changed_at)
            VALUES($id,$account,$revision,$name,$at)
            """;
        command.Parameters.AddWithValue("$id", before.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$revision", before.Revision);
        command.Parameters.AddWithValue("$name", before.Name);
        command.Parameters.AddWithValue("$at", checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        command.ExecuteNonQuery();
    }
}
