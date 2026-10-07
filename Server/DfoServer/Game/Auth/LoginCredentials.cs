namespace DfoServer.Game.Auth;

public static class LoginCredentials
{
    public const string DevelopmentAccount = "10000";
    public const string DevelopmentDigest = "e10adc3949ba59abbe56e057f20f883e";
    // 原生账号字符串长度达到 31 时会被替换；只接受无编码歧义的账号字符。
    public static bool IsAccount(string account) => account.Length is >= 1 and <= 30 &&
        account.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-');

    public static bool IsDigest(string digest) => digest.Length == 32 &&
        digest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}
