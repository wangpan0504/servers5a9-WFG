using DfoServer.Game.Accounts;
using System.Net;

namespace DfoServer.Game.Auth;

public enum AuthenticationFailure
{
    None,
    InvalidCredentials,
    AccountDisabled,
    DuplicateAccountOwner
}

public sealed record AuthenticationSettings(DuplicateLoginPolicy DuplicateLoginPolicy, int SessionTtlSeconds, string ProtocolVersion)
{
    public void Validate()
    {
        if (SessionTtlSeconds is < 30 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(SessionTtlSeconds));
        if (ProtocolVersion != "matched-client")
            throw new ArgumentException("Only the configured client protocol is supported", nameof(ProtocolVersion));
        if (DuplicateLoginPolicy != DuplicateLoginPolicy.Reject && DuplicateLoginPolicy != DuplicateLoginPolicy.ReplaceExisting)
            throw new ArgumentException("Unknown duplicate-login policy", nameof(DuplicateLoginPolicy));
    }
}

public sealed record AuthenticatedIdentity(string AccountId, string AuthSessionId, DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc, IPAddress ClientAddress, string ProtocolVersion, long AuthGeneration);
public sealed record AuthenticationResult(bool Succeeded, AuthenticatedIdentity? Identity, AuthenticationFailure Failure)
{
    public static AuthenticationResult Success(AuthenticatedIdentity identity) => new(true, identity, AuthenticationFailure.None);
    public static AuthenticationResult Rejected(AuthenticationFailure failure) => new(false, null, failure);
}
