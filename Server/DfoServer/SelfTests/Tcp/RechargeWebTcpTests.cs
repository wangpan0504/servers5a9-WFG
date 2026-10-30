using System.Text;
using System.Text.Json;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

// 同一隔离游戏夹具闭合帧、内置HTTP、GM用例和真实SQLite；不需要Python或实际玩家库。
internal static class RechargeWebTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source,
            Path.Combine(root, "recharge-web"), resources: resources);
        await fixture.StartAsync();
        using var peer = Open(fixture, "pay-native");
        peer.Create("PayFirst");
        peer.Create("PayHero");
        peer.Select(1);
        var ticket = Ticket();
        int port = fixture.Options.RechargeWebPort;
        That(fixture.Options.GmPort == 0 && fixture.Options.GmWebPort == 0,"GM入口关闭时充值服务仍必须启动");
        {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
            { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
            const string page = "/comm-htdocs/pay/client/dnf/";
            That((await http.GetAsync("/")).StatusCode == System.Net.HttpStatusCode.NotFound,
                "关闭GM网页时只保留充值路由，不需要启动管理接口或GM TCP端口");
            foreach (string path in new[] { page, page + "diamond/" })
                That((await http.GetStringAsync(path)).Contains("id=\"rechargeButton\"", StringComparison.Ordinal), "原生两条路径提供充值页面");
            var status = await Post("status", new { ticket = ticket.Token, ticketKey = ticket.Key });
            That(status.GetProperty("result").GetProperty("account").GetString() == "pay-native", "自动绑定实际登录账号，不使用网页默认10000");
            var paid = await Post("recharge", new { ticket = ticket.Token, ticketKey = ticket.Key, kind = "cera", amount = 1000 });
            That(paid.GetProperty("result").GetProperty("cera").GetInt32() == 1000 &&
                fixture.Query("SELECT account,balance FROM account_currency_balances WHERE currency_kind=15") is [["pay-native", 1000L]],
                "真实HTTP经本机GM提交后才成功，点券归本次票据账号");
            var black = await Post("recharge", new { ticket = ticket.Token, ticketKey = ticket.Key, kind = "black-diamond", amount = 30 });
            That(black.GetProperty("result").GetProperty("blackDiamondExpiresAt").GetInt64() > DateTimeOffset.UtcNow.AddDays(29).ToUnixTimeSeconds(),
                "真实HTTP开通统一黑钻契约并返回实际期限");
            string before = fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account") +
                fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY account,currency_kind");
            await Post("status", new { account = "10000", mode = "account" }, 400);
            await Post("status", new { ticket = "", ticketKey = 0 }, 400);
            await Post("recharge", new { ticket = ticket.Token, ticketKey = ticket.Key, kind = "cera", amount = "1000" }, 400);
            using (var crossOrigin = new HttpRequestMessage(HttpMethod.Post, page + "api/status")
                { Content = new StringContent(JsonSerializer.Serialize(new {ticket=ticket.Token,ticketKey=ticket.Key}), Encoding.UTF8,"application/json") })
            {
                crossOrigin.Headers.Add("X-Recharge-Request","1");
                crossOrigin.Headers.Add("Origin","http://example.invalid");
                That((await http.SendAsync(crossOrigin)).StatusCode==System.Net.HttpStatusCode.Forbidden,
                    "复用站点同源门禁，跨站请求不能使用游戏充值票据");
            }
            await Post("recharge", new { ticket = ticket.Token, ticketKey = ticket.Key, account = "10000", kind = "cera", amount = 1000 }, 400);
            await Post("recharge", new { ticket = ticket.Token, ticketKey = ticket.Key % 65535 + 1, kind = "cera", amount = 1000 }, 409);
            var next = Ticket();
            var premium = peer.Expect(0, 1760).Body;
            That(Enumerable.Range(0, premium[0]).Any(i => premium[1 + i * 9] == 56 &&
                System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(premium.AsSpan(2 + i * 9)) > 0),
                "复用当前主线契约通知刷新黑钻，不把合法1760当成旧版多余包丢弃");
            foreach (byte kind in new byte[] { 0, 1 })
                That(peer.Expect(0, 2).Body[0] == kind, "黑钻刷新沿当前Actor到Player包序，不用旧版窗口刷新替代");
            await Post("status", new { ticket = ticket.Token, ticketKey = ticket.Key }, 409);
            That(fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account") +
                fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY account,currency_kind") == before,
                "篡改账号、错误key和已替换票据不能再次加值");
            peer.Send(63);
            peer.Expect(0, 53, [1, 0xE8, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            peer.Barrier();
            peer.Send(7);
            peer.Batch((1, 7), (0, 2));
            await Post("status", new { ticket = next.Token, ticketKey = next.Key }, 409);
            That(fixture.Query("PRAGMA integrity_check") is [["ok"]], "充值、余额回读与换角后真实文件库保持完整");

            async Task<JsonElement> Post(string path, object body, int expected = 200)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, page + "api/" + path)
                { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
                request.Headers.Add("X-Recharge-Request", "1");
                using var response = await http.SendAsync(request);
                var value = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
                That((int)response.StatusCode == expected && value.GetProperty("ok").GetBoolean() == (expected == 200),
                    "充值HTTP实际成功/拒绝分支: " + path + "/" + expected + "，实际=" + (int)response.StatusCode +
                    (value.TryGetProperty("error", out var error) ? "，原因=" + error.GetString() : ""));
                return value;
            }
        }
        (string Token, uint Key) Ticket()
        {
            peer.Send(65);
            byte[] body = peer.Expect(1, 65).Body;
            int length = checked((int)U32(body, 1));
            That(body[0] == 1 && length is > 0 and < 64 && body.Length == 9 + length,
                "真实CMD65回复完整帧及长度合同，不使用通用成功字节");
            return (Encoding.ASCII.GetString(body, 5, length), U32(body, 5 + length));
        }

    }
}
