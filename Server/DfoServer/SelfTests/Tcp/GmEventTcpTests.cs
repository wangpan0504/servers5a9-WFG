using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class GmEventTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
        await using var fixture = new TcpTestFixture(source with { GmWebPort = port },
            Path.Combine(root, "gm-events"), resources: resources, useDefaultActivities: true);
        var repository = new DfoServer.Persistence.CharacterRepository(fixture.Database);
        using (var owner = new DfoServer.Game.Accounts.AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repository.Create(owner, DfoServer.Game.Characters.CharacterCreation.Prepare("AllEvents", resources.Characters.Get(0),
                resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get));
        await fixture.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync("/api/status");
                if (response.IsSuccessStatusCode) break;
            }
            catch (HttpRequestException) when (attempt < 99) { }
            if (attempt >= 99) throw new InvalidOperationException("GM活动HTTP未就绪");
            await Task.Delay(50);
        }
        var catalogReply = await Get("/api/events/catalog");
        That(catalogReply.Status == HttpStatusCode.OK, "GM活动目录可通过实际HTTP读取");
        var definitions = Data(catalogReply.Body).GetProperty("events").EnumerateArray()
            .ToDictionary(row => row.GetProperty("id").GetInt32());
        That(definitions.TryGetValue(64, out var gold) && gold.GetProperty("supportsSchedule").GetBoolean() &&
            definitions.TryGetValue(2532, out var nolan) && !nolan.GetProperty("supportsSchedule").GetBoolean() &&
            nolan.GetProperty("name").GetString() == "阿拉德谋略战" && nolan.GetProperty("defaultState").GetString() == "默认开启" &&
            definitions.TryGetValue(228, out var onlineGift) && onlineGift.GetProperty("name").GetString() == "累积在线得好礼" &&
            onlineGift.GetProperty("supportsControl").GetBoolean() && !onlineGift.GetProperty("supportsSchedule").GetBoolean() &&
            onlineGift.GetProperty("defaultState").GetString() == "默认开启" &&
            definitions[2257].GetProperty("defaultState").GetString() == "默认开启" &&
            definitions.TryGetValue(2370, out var attendance) && attendance.GetProperty("defaultState").GetString() == "默认开启" &&
            attendance.GetProperty("supportsControl").GetBoolean() && !attendance.GetProperty("supportsSchedule").GetBoolean() &&
            definitions[2208].GetProperty("defaultState").GetString() == "默认开启" &&
            definitions[2208].GetProperty("supportsSchedule").GetBoolean(),
            "当前GM目录将2257、228、2370和2208标为默认开启，保留228和2370只支持开关的能力");
        That(definitions.Values.All(row => !string.IsNullOrWhiteSpace(row.GetProperty("name").GetString()) &&
            !string.IsNullOrWhiteSpace(row.GetProperty("purpose").GetString()) &&
            !string.IsNullOrWhiteSpace(row.GetProperty("conditions").GetString())),
            "当前活动目录提供GM面板所需名称、用途和条件");
        foreach (var (id, defaultActive) in new[] { (64, true), (266, true), (312, true), (2520, true), (2037, true), (2378, true), (2395, true),
            (2532, true), (2257, true), (228, true), (2370, true), (2208, true), (10005, true) })
        {
            await State(id, "auto", defaultActive);
            foreach (string op in new[] { "on", "off", "auto" })
            {
                var reply = await Post(new { id, op, confirm = true });
                That(reply.Status == HttpStatusCode.OK && !reply.Body.Contains("\"ok\":false", StringComparison.Ordinal),
                    $"实际HTTP允许活动{id}执行{op}");
                await State(id, op, op == "on" || op == "auto" && defaultActive);
            }
        }
        string before = Data((await Get("/api/events?id=228")).Body).GetRawText();
        var refused = await Post(new { id = 228, op = "schedule", start = "2026-10-28T00:00:00+08:00",
            end = "2026-10-29T00:00:00+08:00", confirm = true });
        That(refused.Status == HttpStatusCode.BadRequest && refused.Body.Contains("不支持设置起止时间", StringComparison.Ordinal),
            "228即使时间格式和确认合法也明确拒绝schedule");
        That(Data((await Get("/api/events?id=228")).Body).GetRawText() == before,
            "拒绝时间表不改变228开关与revision");
        VerifyAllDefaultsLogin(fixture);
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "GM目录与活动HTTP开关测试后数据库完整");

        async Task State(int id, string mode, bool active)
        {
            var reply = await Get("/api/events?id=" + id);
            var state = Data(reply.Body);
            That(reply.Status == HttpStatusCode.OK && state.GetProperty("eventId").GetInt32() == id &&
                state.GetProperty("active").GetBoolean() == active && state.GetProperty("mode").GetString() == mode,
                $"GET恢复活动{id}的{mode}模式与实际状态");
        }
        async Task<(HttpStatusCode Status, string Body)> Get(string path)
        {
            using var reply = await http.GetAsync(path);
            return (reply.StatusCode, await reply.Content.ReadAsStringAsync());
        }
        async Task<(HttpStatusCode Status, string Body)> Post(object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/events") { Content = JsonContent.Create(body) };
            request.Headers.Add("X-GM-Request", "1");
            using var reply = await http.SendAsync(request);
            return (reply.StatusCode, await reply.Content.ReadAsStringAsync());
        }
        static JsonElement Data(string body)
        {
            using var document = JsonDocument.Parse(body);
            var value = document.RootElement;
            return (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("data", out var data) ? data : value).Clone();
        }
    }
}
