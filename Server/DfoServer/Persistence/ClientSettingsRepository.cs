using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Settings;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed class ClientSettingsRepository(string path) : IClientSettingsStore
{
    readonly SqliteGameDatabase database = new(path);

    public ClientSettingsSnapshot Load(AccountOwners.Lease owner, ushort characterId) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: true);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT a.game_options,c.character_options,g.game_options FROM characters r
            LEFT JOIN account_client_settings a ON a.account=r.account
            LEFT JOIN character_client_settings c ON c.character_id=r.id AND c.account=r.account
            LEFT JOIN character_game_settings g ON g.character_id=r.id AND g.account=r.account
            WHERE r.id=$id AND r.account=$account
            """;
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$account", owner.Account);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidDataException("设置角色不属于当前账号");
        return new ClientSettingsSnapshot(Read(reader, 0), Read(reader, 1), Read(reader, 2));
    });

    public bool SaveGameOptions(AccountOwners.Lease owner, ImmutableArray<byte> payload, ushort? characterId = null,
        ImmutableArray<byte> characterGameOptions = default) => owner.Execute(() =>
    {
        using var db = database.Open();
        var characterPayload = characterGameOptions.IsDefault ? payload : characterGameOptions;
        bool accountChanged, characterChanged;
        // 先读取已提交值；相同设置不申请 writer，也不补写默认行。
        using (var read = db.BeginTransaction(deferred: true))
        using (var command = db.CreateCommand())
        {
            command.Transaction = read;
            command.CommandText = """
                SELECT s.game_options,g.game_options,c.id FROM accounts a
                LEFT JOIN account_client_settings s ON s.account=a.account_id
                LEFT JOIN characters c ON c.id=$id AND c.account=a.account_id
                LEFT JOIN character_game_settings g ON g.character_id=c.id AND g.account=a.account_id
                WHERE a.account_id=$account
                """;
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$id", (object?)characterId ?? DBNull.Value);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                throw new InvalidDataException("设置账号不存在");
            if (characterId.HasValue && reader.IsDBNull(2))
                throw new InvalidDataException("设置角色不属于当前账号");
            accountChanged = reader.IsDBNull(0) || !payload.AsSpan().SequenceEqual((byte[])reader.GetValue(0));
            characterChanged = characterId.HasValue && (reader.IsDBNull(1) || !characterPayload.AsSpan().SequenceEqual((byte[])reader.GetValue(1)));
        }
        if (!accountChanged && !characterChanged)
            return false;
        using var tx = db.BeginTransaction(deferred: false);
        using var update = db.CreateCommand();
        update.Transaction = tx;
        update.Parameters.AddWithValue("$account", owner.Account);
        if (accountChanged)
        {
            update.Parameters.Add("$payload", SqliteType.Blob).Value = payload.ToArray();
            update.CommandText = """
                INSERT INTO account_client_settings(account,game_options) VALUES($account,$payload)
                ON CONFLICT(account) DO UPDATE SET game_options=excluded.game_options
                """;
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidDataException("账号设置未提交");
        }
        if (characterChanged)
        {
            update.Parameters.AddWithValue("$id", characterId!.Value);
            update.Parameters.Add("$characterPayload", SqliteType.Blob).Value = characterPayload.ToArray();
            update.CommandText = """
                INSERT INTO character_game_settings(character_id,account,game_options) VALUES($id,$account,$characterPayload)
                ON CONFLICT(character_id) DO UPDATE SET game_options=excluded.game_options
                WHERE character_game_settings.account=excluded.account
                """;
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidDataException("角色主设置未提交");
        }
        tx.Commit();
        return true;
    });

    public bool SaveCharacterOptions(AccountOwners.Lease owner, ushort characterId, ImmutableArray<byte> payload) => owner.Execute(() =>
    {
        using var db = database.Open();
        using (var read = db.BeginTransaction(deferred: true))
        using (var command = db.CreateCommand())
        {
            command.Transaction = read;
            command.CommandText = """
                SELECT s.character_options FROM characters c
                LEFT JOIN character_client_settings s ON s.character_id=c.id AND s.account=c.account
                WHERE c.id=$id AND c.account=$account
                """;
            command.Parameters.AddWithValue("$id", characterId);
            command.Parameters.AddWithValue("$account", owner.Account);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                throw new InvalidDataException("设置角色不属于当前账号");
            if (!reader.IsDBNull(0) && payload.AsSpan().SequenceEqual((byte[])reader.GetValue(0)))
                return false;
        }
        using var tx = db.BeginTransaction(deferred: false);
        using var update = db.CreateCommand();
        update.Transaction = tx;
        update.CommandText = """
            INSERT INTO character_client_settings(character_id,account,character_options) VALUES($id,$account,$payload)
            ON CONFLICT(character_id) DO UPDATE SET character_options=excluded.character_options
            WHERE character_client_settings.account=excluded.account
            """;
        update.Parameters.AddWithValue("$id", characterId);
        update.Parameters.AddWithValue("$account", owner.Account);
        update.Parameters.Add("$payload", SqliteType.Blob).Value = payload.ToArray();
        if (update.ExecuteNonQuery() != 1)
            throw new InvalidDataException("角色设置未提交");
        tx.Commit();
        return true;
    });

    static ImmutableArray<byte> Read(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? default : [.. (byte[])reader.GetValue(ordinal)];
}
