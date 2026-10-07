using System.Net;

namespace DfoServer.Game.Auth;

// 登录凭据边界：首次登记与既有凭据验证遵循同一合同，调用方不接触存储实现。
public interface ILoginAuthenticator
{
    AuthenticationResult VerifyOrCreateLogin(string accountId, string digest, IPAddress peer, string protocol, TimeSpan ttl);
}
