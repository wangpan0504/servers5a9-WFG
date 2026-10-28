using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

// GM 网页端到端验收：真实 PVF 资源 + 真实 TCP 客户端在线角色，通过 HTTP 逐个调用 GM 接口。
// 每项结果收集到 accept.json（环境变量 GM_ACCEPT_OUT 可改路径）；GM_ACCEPT_HOLD=<目录> 时在 API 阶段后保持服务运行，供外部浏览器脚本使用。
internal static class GmWebAcceptTcpTests
{
    const string Account = "10000";
    sealed record Row(string Feature, string Check, bool Ok, string Detail);

    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
        await using var fixture = new TcpTestFixture(source with { GmWebPort = port }, Path.Combine(root, "gm-accept"), resources: resources);
        var items = resources.ItemResources;
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        var starter = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0));
        using (var owner = owners.Acquire(Account, () => { }))
            foreach (string name in new[] { "AccA", "AccB", "AccC", "AccD" })
            {
                if (name == "AccB")
                {
                    repository.Create(owner, new(name, resources.Towns.Room, resources.Towns.InitialReturn) { Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                        Inventory = starter with { Gold = 1_000_000, Items = [.. starter.Items, new(0, new(InventoryListType.Main, 9), InventoryRewardGrantService.Create(items.Items.Get(18400)))] } });
                    continue;
                }
                var learning = SkillLearning.Initialize(0, resources.Learning.FreeSkills(0, 0), resources.Learning).State!;
                repository.Create(owner, new(name, resources.Towns.Room, resources.Towns.InitialReturn)
                {
                    Level = 1, GrowType = 0, AcceptedStoryLevel = 1, InitialLearning = learning,
                    Skills = learning.Trees[0].Skills.Select(skill => new CharacterSkill(skill.Id, skill.Level)).OrderBy(skill => skill.Id).ToArray(),
                    Inventory = starter with { Gold = 1_000_000, Items = [.. starter.Items,
                        new(0, new(InventoryListType.Main, 9), InventoryRewardGrantService.Create(items.Items.Get(18400))),
                        new(0, new(InventoryListType.Main, 10), InventoryRewardGrantService.Create(items.Items.Get(10400)))] }
                });
            }
        await fixture.StartAsync();

        var rows = new List<Row>();
        void Check(string feature, string check, bool ok, string detail = "") => rows.Add(new(feature, check, ok, detail));
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(60) };
        for (int attempt = 0; attempt < 100; attempt++)
        {
            try { if ((await http.GetAsync("/api/status")).IsSuccessStatusCode) break; } catch (HttpRequestException) { await Task.Delay(100); }
        }
        async Task<(HttpStatusCode Status, string Body)> Send(HttpMethod method, string path, object? body = null, bool header = true, string? origin = null, string? host = null, string? raw = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (raw is not null) request.Content = new StringContent(raw, Encoding.UTF8, "application/json");
            else if (body is not null) request.Content = JsonContent.Create(body);
            if (header && method != HttpMethod.Get) request.Headers.Add("X-GM-Request", "1");
            if (origin is not null) request.Headers.Add("Origin", origin);
            if (host is not null) request.Headers.Host = host;
            using var response = await http.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        async Task<(HttpStatusCode Status, string Body)> Get(string path)
        {
            // 物品/任务名称索引在后台构建，首次请求可能返回 400“正在构建”，验收时重试到就绪。
            for (int attempt = 0; ; attempt++)
            {
                var r = await Send(HttpMethod.Get, path);
                if (!(r.Status == HttpStatusCode.BadRequest && r.Body.Contains("后台构建") || r.Status == HttpStatusCode.Conflict) || attempt >= 180) return r;
                await Task.Delay(1000);
            }
        }
        Task<(HttpStatusCode Status, string Body)> Post(string path, object body) => Send(HttpMethod.Post, path, body);
        bool Ok((HttpStatusCode Status, string Body) r) => r.Status == HttpStatusCode.OK && !r.Body.Contains("\"ok\":false");
        string Short((HttpStatusCode Status, string Body) r) => (int)r.Status + " " + (r.Body.Length > 200 ? r.Body[..200] : r.Body);
        JsonElement Json(string body) { try { return JsonDocument.Parse(body).RootElement.Clone(); } catch (JsonException) { return default; } }
        JsonElement Data((HttpStatusCode Status, string Body) r) { var root = Json(r.Body); return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) ? data : root; }
        async Task Read(string feature, string path, Func<JsonElement, bool>? verify = null)
        {
            var r = await Get(path);
            bool ok = Ok(r) && (verify is null || verify(Data(r)));
            Check(feature, "GET " + path, ok, Short(r));
        }
        async Task Write(string feature, string path, object body, bool expectOk = true, string? expectText = null)
        {
            var r = await Post(path, body);
            bool ok = expectOk ? Ok(r) : !Ok(r);
            if (expectText is not null) ok &= r.Body.Contains(expectText, StringComparison.Ordinal);
            Check(feature, "POST " + path + (expectOk ? "" : "（应拒绝）"), ok, Short(r));
        }
        object Body(string name, object? extra = null, bool confirm = true)
        {
            var map = new Dictionary<string, object?> { ["account"] = Account, ["name"] = name, ["confirm"] = confirm ? true : null };
            if (extra is not null) foreach (var p in extra.GetType().GetProperties()) map[p.Name] = p.GetValue(extra);
            return map;
        }

        async Task WaitOnline(int expected) { for (int i = 0; i < 100; i++) { var rows = Json((await http.GetAsync("/api/online")).Content.ReadAsStringAsync().Result); if (rows.GetArrayLength() == expected) return; await Task.Delay(100); } }

        // 宽松入城：不校验包序（包序由其它 TCP 组严格覆盖），读到场景初始化 124 即视为进入城镇。
        static void EnterTown(TcpPeer client, ushort slot)
        {
            client.Send(8, [255, 255, 2]);
            client.Send(4, Build(w => w.Write(slot)));
            for (int i = 0; i < 400; i++)
            {
                var packet = client.Read();
                if (packet.Kind != 0 || packet.Opcode != 124) continue;
                // 入城主流程在124后固定跟720/721；本组只看管理端，消费掉避免残留。
                client.ReadWeeklyDungeon();
                return;
            }
            throw new InvalidOperationException("未读到入城完成包");
        }

        // ---------- 读接口 ----------
        await Read("总览", "/api/status", d => d.GetProperty("gmWebPort").GetInt32() == port);
        await Read("账号", "/api/accounts", d => d.EnumerateArray().Any(a => a.GetProperty("account").GetString() == Account && a.GetProperty("characters").GetInt32() == 4));
        await Read("账号", $"/api/accounts/{Account}/characters", d => d.GetArrayLength() == 4);
        await Read("总览", "/api/online", d => d.GetArrayLength() == 0);
        await Read("收发包", "/api/packets/status", d => d.GetProperty("gmWebPort").GetInt32() == port &&
            d.GetProperty("packetHttpPort").GetInt32() == 0);
        using (var packetPeer = TcpPeer.Open(fixture))
        {
            var packets = Json((await Get("/api/packets")).Body);
            int connection = packets.EnumerateArray().First(row => row.GetProperty("protocol").GetString() == "GAME")
                .GetProperty("connectionId").GetInt32();
            await Read("收发包", "/api/packets?connectionId=" + connection, d => d.GetArrayLength() > 0 &&
                d.EnumerateArray().All(row => row.GetProperty("connectionId").GetInt32() == connection));
            await Read("收发包", "/api/packets/sessions", d => d.EnumerateArray().Any(row =>
                row.GetProperty("connectionId").GetInt32() == connection));
            Check("收发包", "认证正文脱敏", packets.EnumerateArray().Any(row => row.GetProperty("opcode").GetInt32() == 1 &&
                row.GetProperty("redacted").GetBoolean() && row.GetProperty("bodyHex").GetString() == ""));
        }
        Check("收发包", "网页查询不启用文件抓包", !File.Exists(Path.Combine(fixture.Options.LogDirectory, "packet_log.txt")));
        foreach (string section in new[] { "summary", "inventory", "mails", "wallet", "quests", "skills", "points", "stats", "titles", "quest-search" })
            await Read("角色详情", $"/api/characters/{Account}/AccA/{section}");
        await Read("角色详情", $"/api/characters/{Account}/AccA/growth");
        await Read("角色详情", $"/api/characters/{Account}/AccA/titles?q=general", d => d.GetProperty("total").GetInt32() > 0);
        await Read("角色详情", $"/api/characters/{Account}/AccA/stats", d => d.GetProperty("stats").GetProperty("maxHp").GetDouble() > 0);
        await Read("物品库", "/api/items/categories", d => d.GetProperty("categories").GetArrayLength() > 1);
        await Read("物品库", "/api/items/search?q=18400", d => d.GetProperty("total").GetInt32() >= 1);
        await Read("物品库", "/api/items/18400", d => d.GetProperty("id").GetInt32() == 18400);
        await Read("物品库", "/api/items/18400/preview", d => d.GetProperty("name").GetString()!.Length > 0);
        await Read("物品库", "/api/items/stat-keys", d => d.GetArrayLength() > 5);
        for (int attempt = 0; attempt < 120; attempt++)
        {
            var r = await Get("/api/items/filter?stat=physical_attack");
            if (Ok(r)) { Check("物品库", "高级筛选（索引后台构建后可用）", Data(r).GetProperty("total").GetInt32() > 0, Short(r)); break; }
            if (attempt == 119) Check("物品库", "高级筛选（索引后台构建后可用）", false, Short(r));
            await Task.Delay(1000);
        }
        // Paging / rarity / stat filters / index state against the real PVF (known counts + known items).
        await Read("items-page", "/api/items/search?q=18400&page=1&pageSize=3", d => d.GetProperty("total").GetInt32() == 1 && d.GetProperty("page").GetInt32() == 1 && d.GetProperty("pageSize").GetInt32() == 3 && d.GetProperty("items")[0].GetProperty("id").GetInt32() == 18400 && d.GetProperty("items")[0].GetProperty("rarity").GetInt32() == 0);
        // 原7174项保留，补登的魔兽三件首饰与十四件堆叠道具同为稀有度4。
        await Read("items-page", "/api/items/search?rarity=4&pageSize=5", d => d.GetProperty("total").GetInt32() == 7174 + 3 + 14 && d.GetProperty("items").GetArrayLength() == 5 && d.GetProperty("items").EnumerateArray().All(i => i.GetProperty("rarity").GetInt32() == 4));
        await Read("items-page", "/api/items/search?rarity=4&page=2&pageSize=5", d => d.GetProperty("page").GetInt32() == 2 && d.GetProperty("offset").GetInt32() == 5);
        await Read("items-rarity", "/api/items/filter?rarity=5&stat=separate_attack&pageSize=5", d => d.GetProperty("total").GetInt32() == 3828 && d.GetProperty("items").EnumerateArray().All(i => i.GetProperty("rarity").GetInt32() == 5));
        await Read("items-stat", "/api/items/stat-keys", d => d.EnumerateArray().Any(k => k.GetProperty("key").GetString() == "separate_attack" && k.GetProperty("count").GetInt32() > 0) && d.EnumerateArray().Any(k => k.GetProperty("count").GetInt32() == 0));
        await Read("index", "/api/index-status", d => d.GetProperty("items").GetBoolean() && d.GetProperty("attributes").GetBoolean());
        await Read("quests-page", $"/api/characters/{Account}/AccA/quest-search?category=side&page=1&pageSize=3", d => d.GetProperty("size").GetInt32() == 3 && d.GetProperty("quests").GetArrayLength() <= 3 && d.GetProperty("categories").EnumerateArray().Any(c => c.GetProperty("key").GetString() == "other") && d.GetProperty("statusCounts").TryGetProperty("done", out _) && d.GetProperty("quests").EnumerateArray().All(q => q.GetProperty("category").GetString() == "side"));
        await Read("quests-page", $"/api/characters/{Account}/AccA/quest-search?status=todo&pageSize=500", d => d.GetProperty("size").GetInt32() == 200 || d.GetProperty("size").GetInt32() == 500);
        {
            var auditPage = await http.GetAsync("/api/audit?page=1&pageSize=2");
            Check("audit-page", "X-Total-Count header + page size", auditPage.IsSuccessStatusCode && auditPage.Headers.TryGetValues("X-Total-Count", out var tc) && int.TryParse(tc.First(), out var total) && total >= 0, auditPage.StatusCode.ToString());
        }
        var iconResponse = await Get("/api/items/18400/icon");
        Check("物品库", "图标：200 PNG 或 204（无 ImagePacks2）", iconResponse.Status is HttpStatusCode.OK or HttpStatusCode.NoContent, Short(iconResponse));
        await Read("活动", "/api/events");
        await Read("诺兰常驻", "/api/events?id=2532", d => d.GetProperty("eventId").GetInt32() == 2532 &&
            d.GetProperty("active").GetBoolean() && d.GetProperty("mode").GetString() == "auto");
        await Read("快捷物品", "/api/quick-items");
        await Read("审计", "/api/audit?limit=50");

        // ---------- 离线写接口（AccA）----------
        await Write("邮件发放", "/api/mail/gold", Body("AccA", new { gold = 5000, text = "验收" }));
        await Write("邮件发放", "/api/mail/item", Body("AccA", new { itemId = 18400, count = 1, upgrade = 3, refine = 0, amp = (string?)null }));
        int setIndex = 0;
        for (int candidate = 1; candidate <= 80 && setIndex == 0; candidate++)
            if (Ok(await Post("/api/mail/set", Body("AccA", new { setIndex = candidate, upgrade = 0, refine = 0 })))) setIndex = candidate;
        Check("邮件发放", "POST /api/mail/set（找到可发放套装）", setIndex != 0, "1..80 均被拒绝");
        await Write("邮件发放", "/api/mail/gold", Body("AccA", new { gold = 2_000_000_000L }), expectOk: false, expectText: "上限");
        await Read("邮箱", $"/api/characters/{Account}/AccA/mails", d => d.GetProperty("mails").GetArrayLength() >= 2);
        await Write("金币", "/api/gold", Body("AccA", new { gold = 123456 }));
        await Read("钱包", $"/api/characters/{Account}/AccA/wallet", d => d.GetProperty("gold").GetInt64() == 123456);
        await Write("钱包", "/api/currency", Body("AccA", new { key = "revive", op = "set", amount = 7 }));
        await Write("钱包", "/api/account-currency", new { account = Account, key = "cera", op = "set", amount = 500, confirm = true });
        await Write("钱包", "/api/account-currency", new { account = Account, key = "revive", op = "set", amount = 5, confirm = true }, expectOk: false);
        await Write("账号金库", "/api/vault-gold", Body("AccA", new { op = "set", amount = 100 }), expectOk: false, expectText: "未开通");
        await Write("等级", "/api/level", Body("AccA", new { level = 20 }));
        await Write("等级", "/api/level", Body("AccA", new { level = 96 }), expectOk: false);
        await Write("转职", "/api/job", Body("AccA", new { branch = 0, awakening = 0 }));
        await Write("转职", "/api/job", Body("AccA", new { branch = 9, awakening = 0 }), expectOk: false);
        await Write("SP/TP", "/api/sp", Body("AccA", new { value = 10 }));
        await Write("SP/TP", "/api/tp", Body("AccA", new { value = 20 }));
        await Read("SP/TP", $"/api/characters/{Account}/AccA/points", d => d.GetProperty("bonusSp").GetInt32() == 10 && d.GetProperty("bonusTp").GetInt32() == 20);
        await Write("SP/TP", "/api/sp", Body("AccA", new { value = 50001 }), expectOk: false);
        await Write("任务", "/api/quests/add", Body("AccA", new { quest = 10100 }));
        await Write("任务", "/api/quests/batch", Body("AccA", new { quests = new[] { 10101, 10102 } }));
        await Write("任务", "/api/quests/remove", Body("AccA", new { quest = 10102 }));
        await Write("任务", "/api/quests/chain", Body("AccA", new { quest = 10105 }));
        await Write("任务", "/api/quests/clear", Body("AccA"));
        await Write("称号成就", "/api/titles/complete", Body("AccA", new { spec = "book:general" }));
        await Read("称号成就", $"/api/characters/{Account}/AccA/titles", d => d.GetProperty("completed").GetInt32() > 0);
        await Write("称号成就", "/api/titles/complete", Body("AccA", new { spec = "book:general" }), expectOk: false);
        var inventory = Data(await Get($"/api/characters/{Account}/AccA/inventory"));
        var gear = inventory.GetProperty("items").EnumerateArray().First(row => row.GetProperty("id").GetInt32() == 10400);
        string gearRef = $"{gear.GetProperty("list").GetInt32()}:{gear.GetProperty("slot").GetInt32()}:{gear.GetProperty("uid").GetInt64()}";
        await Write("直接入包", "/api/direct/item", Body("AccA", new { itemId = 18400, count = 1, upgrade = 5, refine = 0, amp = (string?)null }));
        await Write("直接入包", "/api/direct/set", Body("AccA", new { setIndex, upgrade = 0, refine = 0 }));
        await Write("删除物品", "/api/items/delete", Body("AccA", new { refs = new[] { gearRef } }));
        await Write("删除物品", "/api/items/delete", Body("AccA", new { refs = new[] { "0:3:99;x" } }), expectOk: false);
        await Write("清空容器", "/api/items/clear", Body("AccC", new { container = 0 }));
        await Write("清空容器", "/api/items/clear", Body("AccC", new { container = 300 }), expectOk: false);
        await Write("删除邮件", "/api/mails/delete", Body("AccA", new { all = true }), expectOk: false);
        await Write("改名", "/api/characters/rename", Body("AccD", new { newName = "AccDx" }));
        await Write("改名", "/api/characters/rename", Body("AccDx", new { newName = "AccA" }), expectOk: false);
        await Write("改名", "/api/characters/rename", Body("AccDx", new { newName = "a b" }), expectOk: false);
        await Write("删除角色", "/api/characters/delete", Body("AccDx", new { confirmName = "Wrong" }), expectOk: false);
        await Write("删除角色", "/api/characters/delete", Body("AccDx", new { confirmName = "AccDx" }));
        await Write("活动", "/api/events", new { id = 64, op = "on", confirm = true });
        await Write("活动", "/api/events", new { id = 64, op = "off", confirm = true });
        await Write("活动", "/api/events", new { id = 64, op = "auto", confirm = true });
        await Write("活动", "/api/events", new { id = 5, op = "on", confirm = true }, expectOk: false);
        foreach (string op in new[] { "on", "off", "auto" })
        {
            await Write("诺兰常驻", "/api/events", new { id = 2532, op, confirm = true });
            await Read("诺兰常驻", "/api/events?id=2532", d => d.GetProperty("eventId").GetInt32() == 2532 &&
                d.GetProperty("active").GetBoolean() == (op != "off") && d.GetProperty("mode").GetString() == op);
        }
        await Write("快捷物品", "/api/quick-items", new { items = new[] { new { id = 18400, name = "验收", count = 1 } }, confirm = true });
        await Write("踢下线", "/api/kick", new { account = Account, name = "AccA", confirm = true }, expectOk: false, expectText: "不在线");

        // ---------- 在线角色（AccB 真实 TCP 客户端）----------
        using var peer = Open(fixture, Account);
        EnterTown(peer, 1); await WaitOnline(1);
        var online = Data(await Get("/api/online"));
        Check("在线", "在线接口列出真实在线角色", online.GetArrayLength() == 1 && online[0].GetProperty("name").GetString() == "AccB", online.ToString());
        await Write("在线", "/api/direct/item", Body("AccB", new { itemId = 18400, count = 1, upgrade = 0, refine = 0, amp = (string?)null }), expectOk: false, expectText: "游戏中");
        await Write("在线", "/api/items/clear", Body("AccB", new { container = 0 }), expectOk: false, expectText: "游戏中");
        await Write("在线", "/api/mail/gold", Body("AccB", new { gold = 100, text = "在线提醒" }));
        bool alarm = false;
        try { for (int i = 0; i < 400 && !alarm; i++) { var packet = peer.Read(); alarm = packet.Kind == 0 && packet.Opcode == 99; } }
        catch (Exception) { }
        Check("在线", "邮件发放后在线客户端收到 MAILBOX_ALARM", alarm);
        await Write("在线", "/api/currency", Body("AccB", new { key = "revive", op = "set", amount = 4 }));
        await Write("在线", "/api/vault-gold", Body("AccB", new { op = "set", amount = 1 }), expectOk: false);
                await Write("在线", "/api/quests/add", Body("AccB", new { quest = 10100 }));
        var returned = await Post("/api/direct/item", Body("AccB", new { itemId = 18400, count = 1, upgrade = 0, refine = 0, amp = (string?)null, returnFirst = true }));
        Check("在线", "returnFirst：先强制小退再直接入包（响应带 returned 标志）", Ok(returned) && returned.Body.Contains("\"returned\":true"), Short(returned));
        online = Data(await Get("/api/online"));
        Check("在线", "小退后不再在线", online.GetArrayLength() == 0, online.ToString());
        var afterInventory = Data(await Get($"/api/characters/{Account}/AccB/inventory"));
        Check("在线", "小退重试后物品确实入包", afterInventory.GetProperty("items").EnumerateArray().Count(row => row.GetProperty("id").GetInt32() == 18400) >= 2, afterInventory.ToString().Length > 200 ? afterInventory.ToString()[..200] : afterInventory.ToString());
        peer.Dispose();
        using (var second = Open(fixture, Account))
        {
            EnterTown(second, 1); await WaitOnline(1);
            var kick = await Post("/api/kick", new { account = Account, name = "AccB", confirm = true });
            Check("踢下线", "在线踢下线", Ok(kick), Short(kick));
            var afterKick = Data(await Get("/api/online"));
            Check("踢下线", "踢下线后不再在线", afterKick.GetArrayLength() == 0, afterKick.ToString());
        }

        // ---------- 技能名、经验表、在线称号、断开连接 ----------
        var skillList = Data(await Get($"/api/characters/{Account}/AccA/skill-list"));
        // 紧接在小退/踢线之后，账号租约世代可能短暂变化（“账号世代已变化，请重试”），按提示重试几次。
        for (int retry = 0; retry < 8 && skillList.ToString().Contains("账号世代", StringComparison.Ordinal); retry++)
        {
            await Task.Delay(250);
            skillList = Data(await Get($"/api/characters/{Account}/AccA/skill-list"));
        }
        Check("技能名", "skill-list：返回技能 id/名称/等级，至少一个技能有 PVF 名称", skillList.TryGetProperty("skills", out var skills) && skills.ValueKind == JsonValueKind.Array && skills.GetArrayLength() > 0 &&
            skills.EnumerateArray().Any(sk => sk.TryGetProperty("name", out var nm) && nm.GetString() is { } n && !n.StartsWith("技能 ", StringComparison.Ordinal)), skillList.ToString().Length > 300 ? skillList.ToString()[..300] : skillList.ToString());
        Check("技能名", "skill-list：返回 SP/TP", skillList.TryGetProperty("initialized", out var ini) && ini.ValueKind == JsonValueKind.True && skillList.TryGetProperty("sp", out var spv) && spv.ValueKind == JsonValueKind.Number, skillList.ToString().Length > 200 ? skillList.ToString()[..200] : skillList.ToString());
        var expTable = Data(await Get("/api/exp-table"));
        var expLevels = expTable.GetProperty("levels");
        Check("经验", "exp-table：95 级、Lv2=1000、Lv95=3320457250（超过 int 范围的 u64 不溢出）", expLevels.GetArrayLength() == 95 &&
            expLevels[1].GetProperty("start").GetUInt64() == 1000 && expLevels[94].GetProperty("start").GetUInt64() == 3320457250UL, expTable.ToString().Length > 200 ? expTable.ToString()[..200] : expTable.ToString());
        async Task<int> LevelAfterExp(long exp)
        {
            var r = await Post("/api/exp", Body("AccC", new { exp }));
            if (!Ok(r)) return -1;
            return Data(await Get($"/api/characters/{Account}/AccC/summary")).ToString().Contains("level=") ? int.Parse(System.Text.RegularExpressions.Regex.Match(r.Body, "level=(\\d+)").Groups[1].Value) : -1;
        }
        Check("经验", "累计经验 1500 → Lv2", await LevelAfterExp(1500) == 2);
        Check("经验", "累计经验 3320457250 → Lv95，3320457249 → Lv94", await LevelAfterExp(3320457250L) == 95 && await LevelAfterExp(3320457249L) == 94);
        Check("经验", "负经验/超大经验被拒", !Ok(await Post("/api/exp", Body("AccC", new { exp = -1L }))) && !Ok(await Post("/api/exp", Body("AccC", new { exp = 9_000_000_000L }))));
        using (var third = Open(fixture, Account))
        {
            EnterTown(third, 1); await WaitOnline(1);
            var onlineTitle = await Post("/api/titles/complete", Body("AccB", new { spec = "book:general" }));
            Check("在线称号", "在线角色直接完成称号簿（无需小退）", Ok(onlineTitle) && !onlineTitle.Body.Contains("\"returned\":true"), Short(onlineTitle));
            var stillOnline = Data(await Get("/api/online"));
            Check("在线称号", "完成称号后角色仍在线", stillOnline.GetArrayLength() == 1, stillOnline.ToString());
            var again = await Post("/api/titles/complete", Body("AccB", new { spec = "book:general" }));
            Check("在线称号", "内存快照已同步：重复完成被拒（不是写入过期 revision）", !Ok(again) && again.Body.Contains("没有需要完成"), Short(again));
            var onlineSkills = await Get($"/api/characters/{Account}/AccB/skill-list");
            Check("技能名", "在线角色 skill-list 可读", Ok(onlineSkills), Short(onlineSkills));
            var offlineDisconnect = await Post("/api/disconnect", new { account = "99999", confirm = true });
            Check("断开连接", "不在线账号断开被拒", !Ok(offlineDisconnect), Short(offlineDisconnect));
            Check("断开连接", "缺少确认被拒", !Ok(await Post("/api/disconnect", new { account = Account })));
            var cut = await Post("/api/disconnect", new { account = Account, confirm = true });
            Check("断开连接", "在线账号断开成功", Ok(cut), Short(cut));
            await WaitOnline(0);
            Check("断开连接", "断开后不再在线", Data(await Get("/api/online")).GetArrayLength() == 0);
            bool closed = false;
            try { for (int i = 0; i < 50; i++) third.Read(); } catch (Exception) { closed = true; }
            Check("断开连接", "客户端连接被服务端关闭", closed);
        }
        // ---------- 并发与 revision ----------
        var parallel = await Task.WhenAll(Enumerable.Range(1, 10).Select(i => Post("/api/gold", Body("AccA", new { gold = 1000 * i }))));
        Check("并发", "10 个并发设置金币：无 5xx，至少一个成功", parallel.All(r => Ok(r) || r.Status == HttpStatusCode.Conflict) && parallel.Any(Ok), string.Join(" | ", parallel.Select(r => (int)r.Status)));
        var finalGold = Data(await Get($"/api/characters/{Account}/AccA/wallet")).GetProperty("gold").GetInt64();
        Check("并发", "最终金币等于某个请求的值（无撕裂）", finalGold % 1000 == 0 && finalGold is >= 1000 and <= 10000, finalGold.ToString());
        var parallelQuests = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Post("/api/quests/add", Body("AccA", new { quest = 10110 + i }))));
        Check("并发", "8 个并发完成任务：无 5xx", parallelQuests.All(r => (int)r.Status < 500), string.Join(" | ", parallelQuests.Select(r => (int)r.Status)));
        var done = Json(Data(await Get($"/api/characters/{Account}/AccA/quests")).ToString());
        var parallelMixed = await Task.WhenAll(new[] { Post("/api/sp", Body("AccA", new { value = 1 })), Post("/api/tp", Body("AccA", new { value = 2 })),
            Post("/api/level", Body("AccA", new { level = 30 })), Post("/api/gold", Body("AccA", new { gold = 5 })), Post("/api/currency", Body("AccA", new { key = "revive", op = "add", amount = 1 })) });
        Check("并发", "混合并发写同一角色：无 5xx", parallelMixed.All(r => (int)r.Status < 500), string.Join(" | ", parallelMixed.Select(Short)));

        // ---------- 安全与非法参数 ----------
        var evil = await Send(HttpMethod.Get, "/api/accounts", host: "evil.example");
        Check("安全", "非本机 Host 被拒绝", evil.Status == HttpStatusCode.Forbidden, Short(evil));
        evil = await Send(HttpMethod.Post, "/api/gold", Body("AccA", new { gold = 1 }), header: false);
        Check("安全", "缺少 X-GM-Request 被拒绝", evil.Status == HttpStatusCode.Forbidden, Short(evil));
        evil = await Send(HttpMethod.Post, "/api/gold", Body("AccA", new { gold = 1 }), origin: "http://evil.example");
        Check("安全", "跨源 Origin 被拒绝", evil.Status == HttpStatusCode.Forbidden, Short(evil));
        evil = await Send(HttpMethod.Post, "/api/gold", Body("AccA", new { gold = 1 }), origin: "http://localhost:" + port);
        Check("安全", "localhost 同端口 Origin 的行为（记录）", true, Short(evil));
        await Write("安全", "/api/gold", Body("AccA", new { gold = 1 }, confirm: false), expectOk: false);
        foreach (string name in new[] { "AccA;kick AccA", "AccA\nkick", "Acc A", new string('x', 40), "", "../AccA", "AccA\u0000" })
        {
            var r = await Post("/api/gold", Body(name, new { gold = 1 }));
            Check("安全", "非法角色名拒绝: " + name.Replace("\n", "\\n").Replace("\0", "\\0"), r.Status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound || r.Body.Contains("\"ok\":false"), Short(r));
        }
        foreach (string account in new[] { "gm accept", "gmaccept;x", "x\ny" })
        {
            var r = await Post("/api/gold", new { account, name = "AccA", gold = 1, confirm = true });
            Check("安全", "非法账号拒绝: " + account.Replace("\n", "\\n"), r.Status == HttpStatusCode.BadRequest, Short(r));
        }
        foreach (long gold in new long[] { -1, 2147483648L, long.MaxValue })
        {
            var r = await Post("/api/gold", Body("AccA", new { gold }));
            Check("安全", "越界金币拒绝: " + gold, !Ok(r), Short(r));
        }
        string PaddedBody(int padding) => "{\"account\":\"" + Account + "\",\"name\":\"AccA\",\"gold\":1,\"confirm\":true,\"pad\":\"" + new string('a', padding) + "\"}";
        var withinLimit = await Send(HttpMethod.Post, "/api/gold", raw: PaddedBody(40000));
        Check("安全", "128KiB内合法大请求仍可处理", Ok(withinLimit), Short(withinLimit));
        string oversizedBody = PaddedBody(128 * 1024);
        Check("安全", "超大请求确实超过128KiB字节限制", Encoding.UTF8.GetByteCount(oversizedBody) > 128 * 1024);
        string beforeOversized = fixture.Snapshot("SELECT id,gold,revision FROM characters ORDER BY id");
        string auditBeforeOversized = File.ReadAllText(Path.Combine(fixture.Options.DataDirectory, "gm-audit.jsonl"));
        var big = await Send(HttpMethod.Post, "/api/gold", raw: oversizedBody);
        Check("安全", "超大请求体被拒绝", big.Status is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest, Short(big));
        Check("安全", "超大请求拒绝不改变角色金币或revision", beforeOversized == fixture.Snapshot("SELECT id,gold,revision FROM characters ORDER BY id"));
        Check("安全", "超大请求在命令执行前拒绝且不写GM审计", auditBeforeOversized == File.ReadAllText(Path.Combine(fixture.Options.DataDirectory, "gm-audit.jsonl")));
        var broken = await Send(HttpMethod.Post, "/api/gold", raw: "{not json");
        Check("安全", "畸形 JSON 返回 4xx 而非 500", (int)broken.Status is >= 400 and < 500, Short(broken));
        var wrongMethod = await Send(HttpMethod.Get, "/api/gold");
        Check("安全", "GET 访问写接口 404/405", wrongMethod.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, Short(wrongMethod));
        var traversal = await Get("/assets/..%2F..%2Fgame.db");
        Check("安全", "资源路径穿越 404", traversal.Status == HttpStatusCode.NotFound, Short(traversal));
        var inject = await Get("/api/items/search?q=" + Uri.EscapeDataString("x; item 1 2 3"));
        Check("安全", "物品搜索关键字含分号不被当作命令拼接（返回 400 或空结果）", inject.Status == HttpStatusCode.BadRequest || Ok(inject), Short(inject));
        var csp = await http.GetAsync("/");
        Check("前端", "/favicon.ico 返回 204（避免控制台 404）", (await http.GetAsync("/favicon.ico")).StatusCode == HttpStatusCode.NoContent);
        Check("安全", "页面带 CSP 与 no-store", csp.Headers.Contains("Content-Security-Policy") && csp.Headers.CacheControl?.NoStore == true);
        var auditAll = Json((await Get("/api/audit?limit=500")).Body);
        var actions = auditAll.ValueKind == JsonValueKind.Array ? auditAll.EnumerateArray().Select(row => row.GetProperty("action").GetString()).ToHashSet() : [];
        foreach (string action in new[] { "mail-gold", "mail-item", "mail-set", "gold", "level", "extra-sp", "extra-tp", "quest-add", "quest-batch", "title-complete", "direct-item", "direct-set", "item-delete", "item-clear", "rename", "character-delete", "force-return", "disconnect" })
            Check("审计", "审计包含 " + action, actions.Contains(action), string.Join(",", actions.Take(40)));
        Check("审计", "被拒绝的请求（缺确认/非法名）不写审计", !auditAll.EnumerateArray().Any(row => row.GetProperty("command").GetString()!.Contains("kick AccA\n")), "");

        // ---------- 审计筛选与导出 ----------
        var byAction = Json((await Get("/api/audit?limit=500&action=gold")).Body);
        Check("审计", "按操作 gold 筛选只返回 gold", byAction.GetArrayLength() > 0 && byAction.EnumerateArray().All(row => row.GetProperty("action").GetString() == "gold"));
        var byAccount = Json((await Get($"/api/audit?limit=500&account={Account}&ok=0")).Body);
        Check("审计", "按账号 + 失败筛选", byAccount.GetArrayLength() > 0 && byAccount.EnumerateArray().All(row => !row.GetProperty("ok").GetBoolean() && row.GetProperty("target").GetString()!.StartsWith(Account)));
        var future = Json((await Get("/api/audit?from=" + Uri.EscapeDataString(DateTimeOffset.Now.AddHours(1).ToString("o")))).Body);
        Check("审计", "时间范围：未来起点无记录", future.GetArrayLength() == 0);
        var csv = await Get("/api/audit?format=csv&limit=50");
        Check("审计", "CSV 导出含表头与记录", csv.Status == HttpStatusCode.OK && csv.Body.Contains("时间,操作者,操作") && csv.Body.Split("\r\n").Length > 5, Short(csv));
        Check("审计", "非法过滤参数/时间被拒", (await Get("/api/audit?action=a%20b")).Status == HttpStatusCode.BadRequest && (await Get("/api/audit?from=not-a-date")).Status == HttpStatusCode.BadRequest);
        Check("审计", "CSV 防公式注入单元格转义", DfoServer.Network.Management.GmAudit.ToCsv([new(DateTimeOffset.Now, "x", "=cmd", "t", "c", true, "", "", "")]).Contains("\"'=cmd\""));
        // ---------- 输出 ----------
        string output = Environment.GetEnvironmentVariable("GM_ACCEPT_OUT") ?? Path.Combine(root, "gm-accept", "accept.json");
        File.WriteAllText(output, JsonSerializer.Serialize(new { port, rows }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        if (Environment.GetEnvironmentVariable("GM_ACCEPT_HOLD") is { Length: > 0 } hold)
        {
            using var holdPeer = Open(fixture, Account);
            EnterTown(holdPeer, 1);
            Directory.CreateDirectory(hold);
            File.WriteAllText(Path.Combine(hold, "ready.txt"), port.ToString());
            for (int wait = 0; wait < 1800 && !File.Exists(Path.Combine(hold, "done.txt")); wait++) await Task.Delay(500);
        }
        foreach (var row in rows.Where(row => !row.Ok)) Console.WriteLine($"ACCEPT FAIL [{row.Feature}] {row.Check} :: {row.Detail}");
        Console.WriteLine($"ACCEPT {rows.Count(row => row.Ok)}/{rows.Count} passed; details {output}");
        That(rows.All(row => row.Ok), "GM 网页端到端验收全部通过");
    }
}
