using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

// 不传赛季参数，沿真实Runtime、HTTP与在线连接验证首次默认和持久关闭。
internal static class DefaultActivityTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        { listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; }
        var clock = new DailyBoosterTestClock(new(2026, 11, 3, 4, 0, 0, TimeSpan.Zero));
        await using var fixture = new TcpTestFixture(source with
        { GmWebPort = port, BattlePassSeasonKey = null, BattlePassSeasonStart = null },
            Path.Combine(root, "default-activities"), gm: true, resources: resources, clock: clock, sharedResourcePath: true);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire("10000", () => { }))
            repository.Create(owner, new NewCharacter("DefaultEvt", resources.Towns.Room, resources.Towns.InitialReturn)
            { Job = 0, GrowType = 33, Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills });
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(15) };
        async Task<JsonElement> Post(string endpoint, object body, bool accepted = true, bool header = true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(body) };
            if (header) request.Headers.Add("X-GM-Request", "1");
            using var response = await http.SendAsync(request);
            string content = await response.Content.ReadAsStringAsync();
            if (!header)
            {
                That(!accepted && response.StatusCode == HttpStatusCode.Forbidden && content.Length == 0,
                    "缺少管理请求头时严格返回空正文403，拒绝在业务执行之前");
                return default;
            }
            using var json = JsonDocument.Parse(content);
            var reply = json.RootElement;
            That((response.IsSuccessStatusCode && reply.GetProperty("ok").GetBoolean()) == accepted,
                "real default activity HTTP result: " + reply);
            return accepted ? reply.GetProperty("data").Clone() : reply.Clone();
        }
        Task<JsonElement> Initialize(int id, bool accepted = true, int? pid = null, bool header = true) => Post(
            "/api/events/default", new { id, confirm = true, expectedPid = pid ?? Environment.ProcessId }, accepted, header);
        Task<JsonElement> Toggle(int id, bool on) => Post("/api/events", new { id, confirm = true, op = on ? "on" : "off" });
        async Task ReadyAsync()
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                try { using var response = await http.GetAsync("/api/status"); if (response.IsSuccessStatusCode) return; }
                catch (HttpRequestException) { }
                await Task.Delay(50);
            }
            throw new TimeoutException("Default activity HTTP did not become ready");
        }
        void Events(TcpPeer peer, bool active)
        {
            var packet = peer.Expect(0, 108);
            That(packet.Protected, "online event replacement remains protected");
            using var input = new MemoryStream(packet.Body);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            var body = output.ToArray();
            ushort count = U16(body, 0);
            That(count is >= 5 and <= 7 && body.Length <= 32768, "bounded complete event catalog");
            ushort[] expected = active ? [2037, 2378, 2395, 2532, 266, 2520] : [2037, 2378, 2395, 2532, 266];
            for (int index = 0; index < expected.Length; index++)
                That(U16(body, 2 + index * 15) == expected[index], "online toggle preserves other event entries");
            That(count == expected.Length || count == expected.Length + 1 && U16(body, 2 + expected.Length * 15) == 10999,
                "only configured promotion may extend the event catalog");
            peer.Expect(0, 706);
        }

        await fixture.StartAsync();
        await ReadyAsync();
        string configuration = Path.Combine(fixture.Options.DataDirectory, "battle-pass-default.json");
        That(File.Exists(configuration), "normal runtime prepares a stable season without CLI parameters");
        string stable = File.ReadAllText(configuration);
        That(fixture.Query("SELECT event_id FROM server_events WHERE event_id IN (312,2520)").Length == 0,
            "ordinary server startup does not initialize defaults before a new launcher connects");
        await Initialize(2520, accepted: false, header: false);
        await Initialize(2520, accepted: false, pid: Environment.ProcessId + 1);
        await Initialize(64, accepted: false);
        foreach (int id in new[] { 312, 2520 })
        {
            var state = await Initialize(id);
            That(state.GetProperty("initialized").GetBoolean() && state.GetProperty("active").GetBoolean(),
                "real first initialization activates the prepared activity");
            var again = await Initialize(id);
            That(!again.GetProperty("initialized").GetBoolean() && again.GetProperty("Revision").GetInt64() == state.GetProperty("Revision").GetInt64(),
                "repeated new launcher connection does not rewrite settings");
        }
        uint end = checked((uint)Convert.ToInt64(fixture.Query("SELECT end_utc FROM battle_pass_season_rules").Single()[0]));
        byte[] original;
        using (var peer = Open(fixture, "10000"))
        {
            peer.ExpectedOpenEvents = [2532, 266, 2520];
            peer.ExpectedBattlePassEnd = end;
            peer.Select(); peer.Expect(0, 1800); original = peer.Expect(0, 1801).Body; peer.Expect(0, 1802); peer.Barrier();
            await Toggle(2520, false); Events(peer, false); peer.Barrier();
            var closed = await Initialize(2520);
            That(!closed.GetProperty("initialized").GetBoolean() && !closed.GetProperty("active").GetBoolean(),
                "a new launcher respects an operator's online shutdown");
            await Toggle(2520, true); Events(peer, true);
            peer.Expect(0, 1800); Bytes(peer.Expect(0, 1801).Body, original, "reopening projects the same season and mission progress");
            peer.Expect(0, 1802); peer.Barrier();
            await Toggle(2520, false); Events(peer, false); peer.Barrier();
        }
        var strongwayOff = await Toggle(312, false);
        var passOff = await Initialize(2520);
        await fixture.StopAsync();
        await fixture.StartAsync("defaults-restart");
        await ReadyAsync();
        That(File.ReadAllText(configuration) == stable && fixture.Query("SELECT season_key FROM battle_pass_season_rules").Length == 1,
            "restart retains the stable season and does not roll player ledgers");
        foreach (var (id, revision) in new[] { (312, strongwayOff.GetProperty("Revision").GetInt64()), (2520, passOff.GetProperty("Revision").GetInt64()) })
        {
            var state = await Initialize(id);
            That(!state.GetProperty("initialized").GetBoolean() && !state.GetProperty("active").GetBoolean() && state.GetProperty("Revision").GetInt64() == revision,
                "both persisted shutdown settings survive restart and launcher reconnection");
        }
        using (var peer = Open(fixture, "10000")) { peer.Select(); peer.Barrier(); }
        await fixture.StopAsync();
    }
}
