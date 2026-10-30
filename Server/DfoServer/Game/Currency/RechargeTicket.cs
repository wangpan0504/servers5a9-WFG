using System.Security.Cryptography;
using DfoServer.Game.Auth;

namespace DfoServer.Game.Currency;

internal sealed record RechargeTicket(string Token, uint Key, DateTimeOffset ExpiresAt,
    long Generation, long SelectionEpoch, ushort CharacterId)
{
    public static RechargeTicket Create(string account, long generation, long selectionEpoch, ushort characterId, DateTimeOffset now)
    {
        if (!LoginCredentials.IsAccount(account))
            throw new InvalidDataException("充值票据账号无效");
        string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new(account + "." + nonce, (uint)RandomNumberGenerator.GetInt32(1, 65536), now.AddMinutes(10),
            generation, selectionEpoch, characterId);
    }

    // 前缀只用于路由到既有在线会话；完整票据仍由该会话的owner验证，不是客户端自报身份。
    public static string Account(string token)
    {
        int dot = token.IndexOf('.');
        if (dot < 1 || dot > 30 || token.Length != dot + 23 || !LoginCredentials.IsAccount(token[..dot]) ||
            token[(dot + 1)..].Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')))
            throw new InvalidDataException("充值票据格式无效，请从游戏内重新打开充值页");
        return token[..dot];
    }
}
