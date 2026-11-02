using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.GameWorld.Channels;

namespace DfoServer.Network.Handlers;

internal sealed class LoginHandler(ServerOptions options, AccountOwners owners, ILoginAuthenticator accounts, ChannelCatalog channels, ServerLog log)
{
    internal sealed class Login(AccountOwners.Lease owner, AuthenticatedIdentity identity, ushort sequence, ChannelEndpoint channel) : IDisposable
    {
        public AccountOwners.Lease Owner { get; } = owner;
        public AuthenticatedIdentity Identity { get; } = identity;
        public ushort Sequence { get; } = sequence;
        public ChannelEndpoint Channel { get; } = channel;
        public void Dispose() => Owner.Dispose();
    }

    public async Task<Login?> AuthenticateAsync(ConnectionContext connection)
    {
        AccountOwners.Lease? owner = null;
        bool transferred = false;
        try
        {
            ushort sequence = 0;
            var channel = connection.ChannelEndpoint ?? new(channels.Entry(ChannelCatalog.PrimaryChannelNumber), options.GamePort);
            await connection.WriteAsync(GameFrame.Build(0, (ushort)NotiPacketType.CHANNELINFO, sequence++, LoginPackets.ChannelInfo(channel.Entry.Identity)));
            var precheck = GameFrame.ParseClient(await connection.ReadAsync());
            if (precheck.Opcode != (ushort)CmdPacketType.LOGIN_PRECHECK)
                throw new InvalidDataException($"Expected native login precheck; received opcode={precheck.Opcode}, length={precheck.Body.Length}");
            string claimed = NativeLoginRequest.Precheck(precheck.Body);
            await connection.WriteAsync(GameFrame.Build(1, (ushort)CmdPacketType.LOGIN_PRECHECK, sequence++, LoginPackets.PrecheckAccepted()));
            var request = GameFrame.ParseClient(await connection.ReadAsync());
            if (request.Opcode != (ushort)CmdPacketType.LOGIN)
                throw new InvalidDataException($"Expected native login; received opcode={request.Opcode}, length={request.Body.Length}");
            var credentials = NativeLoginRequest.Login(request.Body);
            if (!string.Equals(claimed, credentials.Account, StringComparison.Ordinal))
                throw new InvalidDataException("Login account changed after precheck");
            var authenticated = accounts.VerifyOrCreateLogin(credentials.Account, credentials.Digest, connection.Peer,
                options.ProtocolVersion, TimeSpan.FromSeconds(options.SessionTtlSeconds));
            if (!authenticated.Succeeded || authenticated.Identity is null)
            {
                log.Write("auth_rejected", new { Connection = connection.Id, authenticated.Failure });
                return null;
            }
            connection.Stop.ThrowIfCancellationRequested();
            var identity = authenticated.Identity;
            if (!owners.TryAcquire(identity.AccountId, connection.Disconnect, options.DuplicateLoginPolicy, out owner, out var reason) || owner is null)
            {
                log.Write("owner_rejected", new { Connection = connection.Id, reason });
                return null;
            }
            connection.Stop.ThrowIfCancellationRequested();
            owner.Execute(() => true);
            log.Write("authenticated", new { Connection = connection.Id, Account = identity.AccountId, owner.Generation, Channel = channel.Entry,
                UserMode = DfoServer.Game.Session.UserStatePolicy.Mode(channel.Entry.Type).ToString() });
            transferred = true;
            return new(owner, identity, sequence, channel);
        }
        finally
        {
            // 认证中断或 owner 获取后失败，都由当前连接释放；成功时交给协议生命周期。
            if (!transferred)
                owner?.Dispose();
        }
    }
}
