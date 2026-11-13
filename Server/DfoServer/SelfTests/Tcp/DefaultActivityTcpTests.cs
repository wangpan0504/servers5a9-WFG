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
            Path.Combine(root, "default-activities"), gm: true, resources: resources, clock: clock, sharedResourcePath: true, useDefaultActivities: true, monsterMockBattle: false);
        // 本专项保留战令缺省状态，其余奖励活动显式关闭，避免无关日补给改变协议样本。
        var unrelated = new DfoServer.Game.Events.ServerEventService(new ServerEventRepository(fixture.Database), clock);
        foreach (ushort id in new ushort[] { 64, 10005, 2257, 228, 2370, 2208, 2514 }) unrelated.Override(id, false);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire("10000", () => { }))
            repository.Create(owner, new NewCharacter("DefEvents", resources.Towns.Room, resources.Towns.InitialReturn)
            { Job = 0, GrowType = 33, Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills });
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(15) };
        async Task<JsonElement> Post(string endpoint, object body, bool accepted = true, bool header = true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(body) };
            if (header) request.Headers.Add("X-GM-Request", "1");
            using var response = await http.SendAsync(request);
            string responseText = await response.Content.ReadAsStringAsync();
            if (!header)
            {
                That(!accepted && response.StatusCode == HttpStatusCode.Forbidden && responseText.Length == 0,
                    "缺少管理请求头必须返回 403 空正文");
                return default;
            }
            using var json = JsonDocument.Parse(responseText);
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
            // 累计资源默认带原生促销页，沿已有完整校验器消费目录、设计页和战令状态尾段。
            ushort[] expected = active ? [282, 2449, 414, 181, 2532, 266, 2520] : [282, 2449, 414, 181, 2532, 266];
            CashShopTcpTests.AssertPromotionNativeRefresh(peer.Expect(0, 108), 0, false, expected,
                active ? peer.ExpectedBattlePassEnd : null);
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
            // 333由TcpPeer及促销校验器在团本目录后统一插入，此处只传额外活动。
            peer.ExpectedOpenEvents = [282, 2449, 414, 181, 2532, 266, 2520];
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

        // 使用同一份合成旧档覆盖真实默认装配失败，禁止选季失败牵连普通登录或改写玩家账本。
        var selected = JsonSerializer.Deserialize<BattlePassDefaultConfiguration>(stable)!;
        fixture.Sql("""
            INSERT INTO battle_pass_season_rules
            SELECT 'legacy-overlap',start_utc-604800,end_utc-604800,rules_json
            FROM battle_pass_season_rules WHERE season_key=$p0;
            INSERT INTO account_battle_pass_seasons VALUES('10000','legacy-overlap',51400,1,55);
            INSERT INTO account_battle_pass_claims VALUES('10000','legacy-overlap',1,1,
                (SELECT id FROM characters WHERE account='10000' LIMIT 1));
            UPDATE server_events SET override_active=1 WHERE event_id=2520;
            """, selected.SeasonKey);
        File.Delete(configuration);
        string Ledger() => string.Join('\n', fixture.Query("SELECT name FROM sqlite_master WHERE type='table' AND name LIKE '%battle_pass%' ORDER BY name")
            .Select(row => (string)row[0]).Select(table => table + ":" + fixture.Snapshot($"SELECT * FROM {table} ORDER BY rowid")));
        async Task Unavailable(string phase)
        {
            string ledger = Ledger();
            string schedule = fixture.Snapshot("SELECT * FROM server_events WHERE event_id=2520");
            await fixture.StartAsync(phase);
            await ReadyAsync();
            await Initialize(2520, accepted: false);
            await Post("/api/events", new { id = 2520, confirm = true, op = "on" }, accepted: false);
            using (var peer = Open(fixture, "10000")) { peer.Select(); peer.Barrier(); }
            await fixture.StopAsync();
            That(Ledger() == ledger && fixture.Snapshot("SELECT * FROM server_events WHERE event_id=2520") == schedule,
                "不可用默认季不创建新季、不改经验资格领奖或活动开关：" + phase);
            That(fixture.Events().Count(entry => entry.GetProperty("Kind").GetString() == "battle_pass_default_unavailable") == 1,
                "默认季不可用保留唯一原因诊断：" + phase);
        }
        await Unavailable("legacy-overlap");
        That(!File.Exists(configuration), "歧义旧档不能擅自写默认选择");
        File.WriteAllText(configuration, "{}");
        await Unavailable("invalid-default");
        That(File.ReadAllText(configuration) == "{}", "坏配置保留原文供修复");
        File.WriteAllText(configuration, stable);
        string rules = (string)fixture.Query("SELECT rules_json FROM battle_pass_season_rules WHERE season_key=$p0", selected.SeasonKey).Single()[0];
        fixture.Sql("UPDATE battle_pass_season_rules SET rules_json='{}' WHERE season_key=$p0", selected.SeasonKey);
        await Unavailable("frozen-rule-conflict");

        // 相同冻结冲突若由操作者显式选季，仍按配置错误拒绝启动。
        await using (var explicitFixture = new TcpTestFixture(source with
        { BattlePassSeasonKey = selected.SeasonKey, BattlePassSeasonStart = selected.Start },
            Path.Combine(root, "explicit-season-conflict"), resources: resources, clock: clock, sharedResourcePath: true))
        {
            SqliteDatabaseBootstrap.Initialize(explicitFixture.Database);
            explicitFixture.Sql("INSERT INTO battle_pass_season_rules VALUES($p0,$p1,$p2,'{}')",
                selected.SeasonKey, selected.Start.ToUnixTimeSeconds(), (long)end);
            bool rejected = false;
            try { await explicitFixture.StartAsync(); }
            catch (InvalidDataException) { rejected = true; }
            That(rejected, "显式指定赛季规则冲突不得降级为成功启动");
        }
        fixture.Sql("UPDATE battle_pass_season_rules SET rules_json=$p1 WHERE season_key=$p0", selected.SeasonKey, rules);
        await fixture.StartAsync("restored-default");
        await ReadyAsync();
        var restored = await Initialize(2520);
        That(!restored.GetProperty("initialized").GetBoolean() && restored.GetProperty("active").GetBoolean(),
            "恢复默认选择后沿用原活动开关");
        using (var peer = Open(fixture, "10000"))
        {
            peer.ExpectedOpenEvents = [282, 2449, 414, 181, 2532, 266, 2520]; peer.ExpectedBattlePassEnd = end;
            peer.Select(); peer.Expect(0, 1800); peer.Expect(0, 1801); peer.Expect(0, 1802); peer.Barrier();
        }
        await fixture.StopAsync();
        File.Delete(configuration);
        clock.Advance(TimeSpan.FromDays(120));
        await Unavailable("expired-history");
        That(!File.Exists(configuration), "到期旧档不自动滚新季");
    }
}
