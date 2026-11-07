using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Text;
using DfoServer.Network.Management;
using Microsoft.AspNetCore.Http;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class GmWebServerTests
{
    static async Task RunManagementBoundaryAsync()
    {
        const int gmPort = 24103, clientPort = 24104;
        const string server = "192.0.2.10", otherLocal = "198.51.100.10", remote = "192.0.2.55";
        int snapshotReads = 0;
        var addresses = new List<IPAddress> { IPAddress.Parse(otherLocal) };
        var boundary = GmWebServer.CreateRequestBoundary(gmPort, true, () => { snapshotReads++; return addresses; });
        addresses.Add(IPAddress.Parse(remote));
        foreach (var (path, method) in new[] { ("/api/accounts", "GET"), ("/api/debug", "POST"), ("/api/packets", "GET") })
        {
            foreach (string? peer in new[] { remote, (string?)null })
            {
                var request = ManagementRequest(path, method, peer, server, gmPort);
                request.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
                request.Request.Headers["Forwarded"] = "for=127.0.0.1;host=localhost";
                await BoundaryResult(boundary, request, 403, false, "真实远端或缺来源不能伪造本机管理来源：" + path);
            }
            foreach (string peer in new[] { "127.0.0.1", server, otherLocal, "::ffff:198.51.100.10" })
                await BoundaryResult(boundary, ManagementRequest(path, method, peer, server, gmPort), 200, true,
                    "回环、本机LAN、多网卡及映射来源仍可管理：" + peer + path);
            await BoundaryResult(boundary, ManagementRequest(path, method, server, server, clientPort), 403, false,
                "同机也不能借客户端网页端口访问管理：" + path);
            var wrongHostPort = ManagementRequest(path, method, server, server, gmPort);
            wrongHostPort.Request.Host = new HostString(server, clientPort);
            await BoundaryResult(boundary, wrongHostPort, 403, false, "管理Host必须匹配实际端口：" + path);
            var missingHostPort = ManagementRequest(path, method, server, server, gmPort);
            missingHostPort.Request.Host = new HostString(server);
            await BoundaryResult(boundary, missingHostPort, 403, false, "非80管理端口不能省略Host端口：" + path);
        }
        That(snapshotReads == 1, "共同中间件只装配一次网卡快照，后续列表变化不增加远端许可");
        var localhost = ManagementRequest("/api/accounts", "GET", "127.0.0.1", "127.0.0.1", gmPort);
        localhost.Request.Host = new HostString("localhost", gmPort);
        await BoundaryResult(boundary, localhost, 200, true, "真实回环来源保留localhost别名");
        localhost.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        await BoundaryResult(boundary, localhost, 403, false, "远端不能以localhost Host冒充本机");
        var wrongOrigin = ManagementRequest("/api/debug", "POST", server, server, gmPort);
        wrongOrigin.Request.Headers.Origin = "http://evil.invalid";
        await BoundaryResult(boundary, wrongOrigin, 403, false, "本机管理写入仍执行原同源约束");

        foreach (var (path, method) in new[] {
            ("/client/includeImg.htm", "GET"), (GmWebServer.GeometryVersionRoute, "GET"),
            (GmWebServer.GeometryBatchRoute, "POST"), (GmWebServer.RechargeRoot, "GET"),
            (GmWebServer.RechargeRoot + "api/recharge", "POST"), ("/favicon.ico", "GET") })
        {
            var request = ManagementRequest(path, method, remote, server, clientPort);
            request.Request.Headers.Remove("X-GM-Request");
            if (path.StartsWith(GmWebServer.RechargeRoot, StringComparison.Ordinal)) request.Request.Headers["X-Recharge-Request"] = "1";
            await BoundaryResult(boundary, request, 200, true, "远端公开路由继续到达原handler：" + path);
        }
        var missingRechargeHeader = ManagementRequest(GmWebServer.RechargeRoot + "api/recharge", "POST", remote, server, clientPort);
        await BoundaryResult(boundary, missingRechargeHeader, 403, false, "公开充值仍需原充值请求头");

        var failures = new List<string>();
        int failedReads = 0;
        var restricted = GmWebServer.CreateRequestBoundary(gmPort, true,
            () => { failedReads++; throw new NetworkInformationException(); }, failures.Add);
        await BoundaryResult(restricted, ManagementRequest("/api/accounts", "GET", otherLocal, server, gmPort), 403, false,
            "快照失败不能放宽到未确认的多网卡或私网来源");
        await BoundaryResult(restricted, ManagementRequest("/api/accounts", "GET", server, server, gmPort), 200, true,
            "快照失败仍支持同机直接通过实际LAN端点访问");
        await BoundaryResult(restricted, ManagementRequest("/api/accounts", "GET", "127.0.0.1", server, gmPort), 200, true,
            "快照失败仍支持回环管理");
        That(failedReads == 1 && failures.Count == 1 && failures[0].Contains("gm_local_addresses_failed", StringComparison.Ordinal),
            "网卡快照失败只尝试一次并给出可定位诊断");

        var disabled = GmWebServer.CreateRequestBoundary(0, true,
            () => throw new InvalidOperationException("GM关闭不能读取管理网卡快照"));
        foreach (string path in new[] { "/api/status", "/api/packets", "/api/packets/sessions", "/api/packets/status" })
            await BoundaryResult(disabled, ManagementRequest(path, "GET", server, server, clientPort), 404, false,
                "关闭管理后共同边界也不执行管理或包handler：" + path);
        await ManagementBoundaryHostsAsync();
    }

    static DefaultHttpContext ManagementRequest(string path, string method, string? peer, string server, int port)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = peer is null ? null : IPAddress.Parse(peer);
        context.Connection.LocalIpAddress = IPAddress.Parse(server);
        context.Connection.LocalPort = port;
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Host = new HostString(server, port);
        context.Request.Headers["X-GM-Request"] = "1";
        context.Request.Headers.Origin = "http://" + context.Request.Host.Value;
        return context;
    }

    static async Task BoundaryResult(Func<HttpContext, RequestDelegate, Task> boundary, DefaultHttpContext context,
        int expectedStatus, bool expectedHandler, string description)
    {
        int handlerCalls = 0;
        context.Response.StatusCode = 200;
        // 直接执行产品Map注册的同一中间件委托；拒绝必须发生在任何读、写或包查询handler之前。
        await boundary(context, _ => { handlerCalls++; return Task.CompletedTask; });
        That(context.Response.StatusCode == expectedStatus && handlerCalls == (expectedHandler ? 1 : 0), description);
    }

    static async Task ManagementBoundaryHostsAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "gm-boundary-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hosts = new List<Task>();
        var signatures = new Dictionary<string, byte[]> { ["map/boundary.map"] = new byte[32] };
        var snapshot = new MapGeometryHttpSnapshot(signatures);
        try
        {
            // 分端口、共端口和GM关闭均使用真实隔离Kestrel，capture始终非空。
            foreach (int mode in new[] { 1, 2, 0 })
            {
                int clientPort = FreeWebPort();
                int gmPort = mode == 0 ? 0 : mode == 2 ? clientPort : FreeWebPort();
                for (int attempt = 0; mode == 1 && gmPort == clientPort && attempt < 10; attempt++) gmPort = FreeWebPort();
                That(mode != 1 || gmPort != clientPort, "隔离分端口夹具取得不同端口");
                var data = new FakeData(Path.Combine(root, "audit-" + mode + ".jsonl"));
                using var capture = new Network.PacketCapture(null, retainRecent: true);
                using var stop = new CancellationTokenSource();
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var host = GmWebServer.RunWithRechargeAsync(gmPort, clientPort, data, stop.Token, capture,
                    new ServerOptions { ListenAddress = "127.0.0.2", GmWebPort = 1 }, started: ready.SetResult,
                    geometry: path => signatures.GetValueOrDefault(path), geometrySnapshot: snapshot);
                hosts.Add(host);
                try
                {
                    await AwaitWebStart(host, ready.Task);
                    That(await ready.Task, "管理边界夹具公开HTTP绑定成功");
                    using var client = WebClient("127.0.0.2", clientPort);
                    foreach (string path in new[] { "/client/includeImg.htm", GmWebServer.RechargeRoot,
                        "/client/displaycompat-world?map=map/boundary.map", GmWebServer.GeometryVersionRoute })
                    {
                        using var response = await client.GetAsync(path);
                        That(response.StatusCode == HttpStatusCode.OK, "管理开关和端口拆分不破坏公开页面或几何：" + mode + path);
                    }
                    using (var body = new ByteArrayContent(GeometryBoundaryRequest(snapshot)))
                    {
                        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                        using var response = await client.PostAsync(GmWebServer.GeometryBatchRoute, body);
                        That(response.StatusCode == HttpStatusCode.OK && (await response.Content.ReadAsByteArrayAsync()).AsSpan(0, 4).SequenceEqual("DGS1"u8),
                            "公开几何批量POST保留原只读例外，无须GM请求头");
                    }
                    int blockedStatus = mode == 0 ? 404 : mode == 1 ? 403 : 200;
                    foreach (string path in new[] { "/api/accounts", "/api/packets", "/api/packets/sessions", "/api/packets/status" })
                    {
                        using var response = await client.GetAsync(path);
                        That((int)response.StatusCode == blockedStatus, "真实客户端端口管理/抓包边界：" + mode + path);
                    }
                    if (mode != 2)
                    {
                        int before = data.Commands.Count;
                        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/debug")
                        { Content = JsonContent.Create(new { key = "pc-room", value = "on", confirm = true }) };
                        request.Headers.Add("X-GM-Request", "1");
                        using var response = await client.SendAsync(request);
                        That((int)response.StatusCode == blockedStatus && data.Commands.Count == before &&
                            !File.Exists(Path.Combine(root, "audit-" + mode + ".jsonl")),
                            "真实公开端口拒绝管理写入，命令和审计均无变化");
                    }
                    if (gmPort != 0)
                    {
                        using var gm = WebClient("127.0.0.2", gmPort);
                        using var read = await gm.GetAsync("/api/accounts");
                        That(read.StatusCode == HttpStatusCode.OK, "真实GM端口按RunCore实际参数开放，不使用options或状态返回猜端口");
                        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/debug")
                        { Content = JsonContent.Create(new { key = "pc-room", value = "on", confirm = true }) };
                        request.Headers.Add("X-GM-Request", "1");
                        using var written = await gm.SendAsync(request);
                        That(written.StatusCode == HttpStatusCode.OK && data.Commands.Contains("pc-room on"), "合法本机管理写入仍到达原命令与审计链");
                    }
                }
                finally { stop.Cancel(); await host.WaitAsync(TimeSpan.FromSeconds(10)); }
            }
        }
        finally
        {
            // 停机未结束时保留本轮资料，不能删除仍可能被在途handler使用的目录。
            if (hosts.All(host => host.IsCompleted)) Directory.Delete(root, recursive: true);
        }
    }

    static byte[] GeometryBoundaryRequest(MapGeometryHttpSnapshot snapshot)
    {
        using var body = new MemoryStream();
        using var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true);
        writer.Write("DGB1"u8);
        writer.Write(snapshot.Version);
        writer.Write((ushort)1);
        byte[] path = Encoding.UTF8.GetBytes("map/boundary.map");
        writer.Write((ushort)path.Length);
        writer.Write(path);
        return body.ToArray();
    }
}
