using DfoServer.Game.Auth;
using System.Collections.Immutable;

namespace DfoServer.Persistence;

public sealed class AuthenticationRepository(string path) : IAccountCredentialStore
{
    readonly SqliteGameDatabase database = new(path);

    public AccountCredential? Find(string accountId)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT salt,password_hash,iterations,disabled,revision FROM accounts WHERE account_id=$account";
        cmd.Parameters.AddWithValue("$account", accountId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return new(new(((byte[])reader[0]).ToImmutableArray(), ((byte[])reader[1]).ToImmutableArray(), reader.GetInt32(2)),
            reader.GetInt32(3) != 0, reader.GetInt64(4));
    }

    public bool TryCreate(string accountId, PasswordCredential password)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        // 只忽略账号唯一键冲突，其他约束或 I/O 故障不能伪装成已有账号。
        cmd.CommandText = "INSERT INTO accounts(account_id,salt,password_hash,iterations) VALUES($account,$salt,$hash,$iterations) ON CONFLICT(account_id) DO NOTHING";
        cmd.Parameters.AddWithValue("$account", accountId);
        cmd.Parameters.AddWithValue("$salt", password.Salt.ToArray());
        cmd.Parameters.AddWithValue("$hash", password.Hash.ToArray());
        cmd.Parameters.AddWithValue("$iterations", password.Iterations);
        return cmd.ExecuteNonQuery() == 1;
    }

    public bool SetDisabled(string accountId, bool disabled)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE accounts SET disabled=$disabled,revision=revision+1 WHERE account_id=$account";
        cmd.Parameters.AddWithValue("$disabled", disabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$account", accountId);
        return cmd.ExecuteNonQuery() == 1;
    }

    public bool ChangePassword(string accountId, PasswordCredential password)
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE accounts SET salt=$salt,password_hash=$hash,iterations=$iterations,revision=revision+1 WHERE account_id=$account";
        cmd.Parameters.AddWithValue("$salt", password.Salt.ToArray());
        cmd.Parameters.AddWithValue("$hash", password.Hash.ToArray());
        cmd.Parameters.AddWithValue("$iterations", password.Iterations);
        cmd.Parameters.AddWithValue("$account", accountId);
        bool changed = cmd.ExecuteNonQuery() == 1;
        tx.Commit();
        return changed;
    }
}
