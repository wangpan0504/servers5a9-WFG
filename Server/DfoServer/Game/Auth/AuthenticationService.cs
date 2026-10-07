using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;

namespace DfoServer.Game.Auth;

public sealed class AuthenticationService : ILoginAuthenticator
{
    const int SaltSize = 16, HashSize = 32, DefaultIterations = 120000;
    readonly IAccountCredentialStore store;
    readonly int iterations;

    public AuthenticationService(IAccountCredentialStore store, int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (iterations < 100000)
            throw new ArgumentOutOfRangeException(nameof(iterations));
        this.store = store;
        this.iterations = iterations;
    }

    public void Create(string accountId, string password)
    {
        string account = Normalize(accountId);
        ValidatePassword(password);
        if (!store.TryCreate(account, Derive(password)))
            throw new InvalidOperationException("Account already exists");
    }

    public void EnsureDevelopmentAccount()
    {
        if (store.Find(LoginCredentials.DevelopmentAccount) is null)
            // 并发初始化只竞争缺失行，已有密码、禁用状态及 revision 不变。
            store.TryCreate(LoginCredentials.DevelopmentAccount, Derive(LoginCredentials.DevelopmentDigest));
    }

    public AuthenticationResult VerifyOrCreateLogin(string accountId, string digest, IPAddress peer, string protocol, TimeSpan ttl)
    {
        if (!LoginCredentials.IsAccount(accountId) || !LoginCredentials.IsDigest(digest) || ttl <= TimeSpan.Zero)
            return AuthenticationResult.Rejected(AuthenticationFailure.InvalidCredentials);
        string account = Normalize(accountId);
        digest = digest.ToLowerInvariant();
        var stored = store.Find(account);
        if (stored is null)
        {
            // 耗时派生不持有数据库连接/写锁；冲突后必须重新读取并验证胜出的持久凭据。
            store.TryCreate(account, Derive(digest));
            stored = store.Find(account);
        }
        return Verify(account, digest, stored, peer, protocol, ttl);
    }

    public AuthenticationResult Verify(string accountId, string password, IPAddress clientAddress, string protocolVersion, TimeSpan ttl)
    {
        if (string.IsNullOrEmpty(password))
            return AuthenticationResult.Rejected(AuthenticationFailure.InvalidCredentials);
        string account = Normalize(accountId);
        return Verify(account, password, store.Find(account), clientAddress, protocolVersion, ttl);
    }

    static AuthenticationResult Verify(string account, string password, AccountCredential? stored, IPAddress peer, string protocol, TimeSpan ttl)
    {
        if (stored is null)
            return AuthenticationResult.Rejected(AuthenticationFailure.InvalidCredentials);
        if (stored.Disabled)
            return AuthenticationResult.Rejected(AuthenticationFailure.AccountDisabled);
        if (!CryptographicOperations.FixedTimeEquals(stored.Password.Hash.AsSpan(),
            Hash(password, stored.Password.Salt.AsSpan(), stored.Password.Iterations)) || ttl <= TimeSpan.Zero)
            return AuthenticationResult.Rejected(AuthenticationFailure.InvalidCredentials);
        var now = DateTimeOffset.UtcNow;
        return AuthenticationResult.Success(new(account, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), now, now.Add(ttl), peer, protocol, stored.Revision));
    }

    public void Disable(string accountId) => SetDisabled(accountId, true);
    public void Enable(string accountId) => SetDisabled(accountId, false);
    void SetDisabled(string accountId, bool disabled)
    {
        if (!store.SetDisabled(Normalize(accountId), disabled))
            throw new KeyNotFoundException("Account not found");
    }

    public void ChangePassword(string accountId, string password)
    {
        ValidatePassword(password);
        string account = Normalize(accountId);
        if (!store.ChangePassword(account, Derive(password)))
            throw new KeyNotFoundException("Account not found");
    }

    PasswordCredential Derive(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        return new(salt.ToImmutableArray(), Hash(password, salt, iterations).ToImmutableArray(), iterations);
    }

    static byte[] Hash(string password, ReadOnlySpan<byte> salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashSize);

    static string Normalize(string account)
    {
        if (string.IsNullOrWhiteSpace(account) || account.Length > 64)
            throw new ArgumentException("Invalid account id", nameof(account));
        return account.Trim().ToLowerInvariant();
    }

    static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 256)
            throw new ArgumentException("Invalid password", nameof(password));
    }
}
