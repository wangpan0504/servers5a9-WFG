using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;

namespace DfoServer.Network.Management;

internal static partial class GmWebServer
{
    // 与真实路由共用此委托；网卡快照只在装配时读取，不在请求中枚举或解析域名。
    internal static Func<HttpContext, RequestDelegate, Task> CreateRequestBoundary(int managementPort, bool clientPages,
        Func<IEnumerable<IPAddress>>? localAddresses = null, Action<string>? reportSnapshotFailure = null)
    {
        var local = new HashSet<IPAddress>();
        if (managementPort != 0)
        {
            try
            {
                local = (localAddresses?.Invoke() ?? NetworkInterface.GetAllNetworkInterfaces()
                    .SelectMany(network => network.GetIPProperties().UnicastAddresses).Select(item => item.Address))
                    .Select(NormalizeAddress).Where(address => (address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                        && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any)).ToHashSet();
            }
            catch (Exception error) when (error is NetworkInformationException or SocketException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                (reportSnapshotFailure ?? Console.Error.WriteLine)("[WARN] [gm_local_addresses_failed] Local interface snapshot unavailable; "
                    + "only loopback and the actual local endpoint remain allowed. " + error.GetType().Name + ": " + error.Message);
            }
        }
        return async (context, next) =>
        {
            string host = context.Request.Host.Host;
            var peer = context.Connection.RemoteIpAddress is { } remote ? NormalizeAddress(remote) : null;
            var listener = context.Connection.LocalIpAddress is { } endpoint ? NormalizeAddress(endpoint) : null;
            bool loopback = peer is not null && IPAddress.IsLoopback(peer);
            bool localhost = loopback && string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
            bool listenerHost = IPAddress.TryParse(host, out var address) && NormalizeAddress(address).Equals(listener);
            if (!localhost && !listenerHost)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            bool recharge = context.Request.Path.StartsWithSegments(RechargeRoot.TrimEnd('/'), StringComparison.Ordinal);
            bool clientPath = clientPages && (context.Request.Path.StartsWithSegments("/client", StringComparison.Ordinal) ||
                recharge || context.Request.Path == "/favicon.ico");
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Content-Security-Policy"] = clientPath
                ? "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'"
                : "default-src 'self'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
            if (!clientPath)
            {
                if (managementPort == 0)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                bool sameMachine = peer is not null && listener is not null && (loopback || peer.Equals(listener) || local.Contains(peer));
                if (!sameMachine || context.Connection.LocalPort != managementPort ||
                    (context.Request.Host.Port ?? 80) != context.Connection.LocalPort)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                // 保留充值票据和几何只读POST的原合同；管理来源不能由这些请求头替代证明。
                string? origin = context.Request.Headers.Origin;
                bool geometryQuery = clientPages && HttpMethods.IsPost(context.Request.Method) && context.Request.Path == GeometryBatchRoute;
                if ((!geometryQuery && context.Request.Headers[recharge ? "X-Recharge-Request" : "X-GM-Request"] != "1") ||
                    origin is not null && origin != "http://" + context.Request.Host.Value)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }
            await next(context);
        };
    }

    private static IPAddress NormalizeAddress(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
