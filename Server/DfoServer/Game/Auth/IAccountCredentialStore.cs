using System.Collections.Immutable;

namespace DfoServer.Game.Auth;

public sealed record PasswordCredential(ImmutableArray<byte> Salt, ImmutableArray<byte> Hash, int Iterations);
public sealed record AccountCredential(PasswordCredential Password, bool Disabled, long Revision);

// 输入账号已归一化，凭据只含盐和派生值。唯一键冲突返回 false，其他存储错误继续抛出。
public interface IAccountCredentialStore
{
    AccountCredential? Find(string accountId);
    bool TryCreate(string accountId, PasswordCredential password);
    bool ChangePassword(string accountId, PasswordCredential password);
    bool SetDisabled(string accountId, bool disabled);
}
