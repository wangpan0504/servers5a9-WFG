using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using DfoServer.Network.Management;
using DfoServer.Testing;

namespace DfoServer;

internal static class GmWebServerTests
{
    sealed class FakeData(string audit) : IGmWebData
    {
        public List<string> Commands { get; } = [];
        public List<string> Gold { get; } = [];
        public List<string> Notified { get; } = [];
        public List<string> Returned { get; } = [];
        public bool InGame = true;
        public List<string> Disconnected { get; } = [];
        public bool Disconnect(string account) { Disconnected.Add(account); return account == "acct"; }
        public GmAudit Audit { get; } = new(audit);
        public GmWebStatus Status() => new(1, DateTimeOffset.UnixEpoch, 1, 2, 3, 4, "x.pvf", "data", 1);
        public OnlineCharacterView[] Online() => [new("acct", 7, "Hero", true, "Town", "Town")];
        public GmWebAccount[] Accounts() => [new("acct", false, 3, 1)];
        public GmWebCharacter[]? Characters(string account) => account == "acct" ? [new(7, 0, "Hero", 1, 0, 50, 99, 4)] : null;
        public string Command(string line)
        {
            Commands.Add(line);
            if (line.Contains(" items ")) return "OK {\"name\":\"Hero\",\"gold\":99,\"items\":[{\"list\":0,\"slot\":1,\"id\":100,\"name\":\"剑\",\"count\":1}]}";
            if (line == "exp-table") return "OK {\"max\":3,\"levels\":[{\"level\":1,\"start\":0,\"span\":1000},{\"level\":2,\"start\":1000,\"span\":2000},{\"level\":3,\"start\":3000,\"span\":0}]}";
            if (line.StartsWith("item-filter")) return "OK {\"total\":0,\"offset\":0,\"items\":[]}";
            if (line.Contains(" stats ")) return "OK {\"stats\":{}}";
            if (line.Contains(" title-list ")) return "OK {\"total\":0,\"quests\":[]}";
            if (InGame && line.Contains(" title-complete ")) return "ERR 角色正在游戏中，客户端背包或邮箱不会即时刷新；请先回到选角再执行";
            if (line.StartsWith("item-search")) return "OK {\"total\":1,\"offset\":0,\"items\":[{\"id\":100,\"name\":\"剑\",\"tag\":\"x\",\"minLevel\":1,\"rarity\":2,\"set\":0}]}";
            if (line.Contains(" show ")) return "OK Hero id=7 level=" + (Commands.Count(c => c.Contains(" level ")) == 0 ? 50 : 60);
            if (line.Contains(" gold ") && line.Contains("999")) return "ERR 金币失败";
            if (InGame && (line.Contains(" item-delete ") || line.Contains(" item-clear ") || line.Contains(" mail-delete ") || line.EndsWith(" direct"))) return "ERR 角色正在游戏中，客户端背包或邮箱不会即时刷新；请先回到选角再执行";
            if (line.Contains(" points ")) return "OK {\"bonusSp\":5,\"bonusTp\":6,\"sp\":7,\"tp\":8}";
            return "OK {\"target\":\"mail\",\"ok\":1}";
        }
        public Task<bool> ForceReturnAsync(string account, string? name) { Returned.Add(account + "/" + name); if (account == "acct") InGame = false; return Task.FromResult(account == "acct"); }
        public void NotifyMailArrived(string account, string name) => Notified.Add(account + "/" + name);
        public string Quick = "[]";
        public string ReadQuickItems() => Quick;
        public void WriteQuickItems(string json) => Quick = json;
        public byte[]? ItemIcon(int id) => id == 7 ? [137, 80, 78, 71] : null;
        public object? ItemPreview(int id) => id == 7 ? new { id, name = "测试", stats = new[] { "力量 +3" } } : null;
        public string DeliverGoldMail(string account, string name, uint gold, string text) { Gold.Add($"{account}/{name}/{gold}/{text}"); return "OK {\"gold\":" + gold + "}"; }
    }

    public static async Task RunAsync()
    {
        int before = TestCheck.Count;
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
        string directory = Path.Combine(Path.GetTempPath(), "gm-web-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string auditPath = Path.Combine(directory, "gm-audit.jsonl");
        var data = new FakeData(auditPath);
        using var stop = new CancellationTokenSource();
        var server = GmWebServer.RunAsync(port, data, stop.Token);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
        HttpResponseMessage page = null!;
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try { page = await http.GetAsync("/"); break; }
            catch (HttpRequestException) { await Task.Delay(100); }
        }
        string html = await page.Content.ReadAsStringAsync();
        TestCheck.That(page is { IsSuccessStatusCode: true } && html.Contains("S5A9 GM") && html.Contains("/assets/app.js"), "GM 页面可访问并引用模块脚本");
        TestCheck.That(page.Headers.CacheControl?.NoStore == true, "GM 响应禁止缓存");
        TestCheck.That(page.Headers.Contains("Content-Security-Policy"), "GM 页面带内容安全策略");
        foreach (string asset in new[] { "app.js", "api.js", "ui.js", "picker.js", "drawer-char.js", "p-overview.js", "p-accounts.js", "p-grant.js",
            "p-quests.js", "p-mail.js", "p-items.js", "p-audit.js", "gm.css" })
        {
            var response = await http.GetAsync("/assets/" + asset);
            TestCheck.That(response.IsSuccessStatusCode && response.Content.Headers.ContentType!.MediaType == (asset.EndsWith("css") ? "text/css" : "text/javascript"), "内嵌资源 " + asset);
        }
        TestCheck.That((await http.GetAsync("/assets/missing.js")).StatusCode == HttpStatusCode.NotFound, "未知资源 404");
        TestCheck.That((await http.GetAsync("/assets/..%2Findex.html")).StatusCode == HttpStatusCode.NotFound, "资源名白名单拒绝路径穿越");
        var status = await http.GetFromJsonAsync<JsonElement>("/api/status");
        TestCheck.That(status.GetProperty("pid").GetInt32() == 1 && status.GetProperty("gmWebPort").GetInt32() == 4, "状态接口返回进程与端口信息");
        var accounts = await http.GetFromJsonAsync<JsonElement>("/api/accounts");
        TestCheck.That(accounts.GetArrayLength() == 1 && accounts[0].GetProperty("account").GetString() == "acct" && !accounts[0].TryGetProperty("passwordHash", out _), "账号列表只含摘要字段");
        var online = await http.GetFromJsonAsync<JsonElement>("/api/online");
        TestCheck.That(online[0].GetProperty("name").GetString() == "Hero", "在线角色接口");
        var characters = await http.GetFromJsonAsync<JsonElement>("/api/accounts/acct/characters");
        TestCheck.That(characters[0].GetProperty("level").GetInt32() == 50, "角色列表接口");
        TestCheck.That((await http.GetAsync("/api/accounts/missing/characters")).StatusCode == HttpStatusCode.NotFound, "未知账号返回 404");
        using var rebind = new HttpRequestMessage(HttpMethod.Get, "/api/accounts") { Headers = { Host = "evil.example" } };
        TestCheck.That((await http.SendAsync(rebind)).StatusCode == HttpStatusCode.Forbidden, "非本机 Host 头被拒绝（防 DNS 重绑定）");

        // 只读详情与物品库：转成已有 GM 查询命令。
        var inventory = await http.GetFromJsonAsync<JsonElement>("/api/characters/acct/Hero/inventory");
        TestCheck.That(inventory.GetProperty("ok").GetBoolean() && inventory.GetProperty("data").GetProperty("items")[0].GetProperty("id").GetInt32() == 100
            && data.Commands.Contains("account acct items Hero"), "背包只读接口走 items 命令");
        var summary = await http.GetFromJsonAsync<JsonElement>("/api/characters/acct/Hero/summary");
        TestCheck.That(summary.GetProperty("text").GetString()!.StartsWith("Hero id=7"), "文本结果以 text 返回");
        TestCheck.That((await http.GetAsync("/api/characters/acct/Hero/secret")).StatusCode == HttpStatusCode.NotFound, "未知分区 404");
        TestCheck.That((await http.GetAsync("/api/characters/acct/bad%20name/inventory")).StatusCode == HttpStatusCode.BadRequest, "含空白的角色名被拒绝（防命令注入）");
        var search = await http.GetFromJsonAsync<JsonElement>("/api/items/search?offset=50&min=1&max=40&rarity=2&tag=x&q=%E5%89%91");
        TestCheck.That(search.GetProperty("data").GetProperty("total").GetInt32() == 1 && data.Commands.Contains("item-search 50/50 1 40 2 x 剑"), "物品库搜索转成 item-search");
        await http.GetAsync("/api/items/search?page=3&pageSize=20&q=x");
        TestCheck.That(data.Commands.Contains("item-search 40/20 0 0 * * x"), "items page/pageSize -> offset/size token: ");
        await http.GetAsync("/api/items/search?page=2&pageSize=9999&q=x");
        TestCheck.That(data.Commands.Contains("item-search 200/200 0 0 * * x"), "pageSize clamped to 200");
        TestCheck.That((await http.GetAsync("/api/items/search?q=a%0Ab")).StatusCode == HttpStatusCode.BadRequest, "含控制字符的关键字被拒绝");
        await http.GetAsync("/api/items/categories");
        TestCheck.That(data.Commands.Contains("item-categories"), "分类接口走 item-categories");

        // 写接口：防跨站、必须二次确认、参数校验。
        async Task<HttpResponseMessage> Post(string path, object body, bool header = true, string? origin = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            if (header) request.Headers.Add("X-GM-Request", "1");
            if (origin is not null) request.Headers.Add("Origin", origin);
            return await http.SendAsync(request);
        }
        int commands = data.Commands.Count;
        var item = new { account = "acct", name = "Hero", itemId = 100, count = 2, upgrade = 3, refine = 4, confirm = true };
        TestCheck.That((await Post("/api/mail/item", item, header: false)).StatusCode == HttpStatusCode.Forbidden, "写接口缺少 X-GM-Request 被拒绝");
        TestCheck.That((await Post("/api/mail/item", item, origin: "http://evil.example")).StatusCode == HttpStatusCode.Forbidden, "写接口跨站 Origin 被拒绝");
        TestCheck.That((await Post("/api/mail/item", new { account = "acct", name = "Hero", itemId = 100, count = 2, upgrade = 3, refine = 4 })).StatusCode == HttpStatusCode.BadRequest
            && data.Commands.Count == commands, "缺少 confirm 不执行");
        TestCheck.That((await Post("/api/mail/item", new { account = "acct", name = "Hero", itemId = 0, count = 2, upgrade = 3, refine = 4, confirm = true })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/mail/item", new { account = "acct", name = "He ro", itemId = 1, count = 1, upgrade = 0, refine = 0, confirm = true })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/mail/item", new { account = "acct", name = "Hero", itemId = 1, count = 1, upgrade = 0, refine = 0, amp = "amp:9:1", confirm = true })).StatusCode == HttpStatusCode.BadRequest
            && data.Commands.Count == commands, "非法物品参数、含空白名称、非法增幅都不执行");
        var sent = await Post("/api/mail/item", item, origin: $"http://127.0.0.1:{port}");
        TestCheck.That(sent.IsSuccessStatusCode && data.Commands.Contains("account acct grant Hero 100 2 3 4 mail"), "邮件发放物品转成 grant ... mail");
        var set = await Post("/api/mail/set", new { account = "acct", name = "Hero", setIndex = 5, upgrade = 0, refine = 0, confirm = true });
        TestCheck.That(set.IsSuccessStatusCode && data.Commands.Contains("account acct set-grant Hero 5 0 0 mail"), "邮件发放套装转成 set-grant ... mail");
        var gold = await Post("/api/mail/gold", new { account = "acct", name = "Hero", gold = 5000, text = "补偿", confirm = true });
        TestCheck.That(gold.IsSuccessStatusCode && data.Gold.SequenceEqual(["acct/Hero/5000/补偿"]), "金币邮件走 DeliverGoldMail");
        TestCheck.That((await Post("/api/mail/gold", new { account = "acct", name = "Hero", gold = 0, confirm = true })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/mail/gold", new { account = "acct", name = "Hero", gold = 2_000_000_001L, confirm = true })).StatusCode == HttpStatusCode.BadRequest, "金币范围校验");
        TestCheck.That((await Post("/api/kick", new { account = "acct", name = "Hero", confirm = true })).IsSuccessStatusCode
            && (await Post("/api/kick", new { account = "ghost", confirm = true })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/kick", new { account = "acct", name = "Hero" })).StatusCode == HttpStatusCode.BadRequest, "强制小退：在线成功、离线报错、缺确认拒绝");
        TestCheck.That((await Post("/api/level", new { account = "acct", name = "Hero", level = 96, confirm = true })).StatusCode == HttpStatusCode.BadRequest, "等级范围校验");
        TestCheck.That((await Post("/api/level", new { account = "acct", name = "Hero", level = 60, confirm = true })).IsSuccessStatusCode
            && data.Commands.Contains("account acct level Hero 60"), "等级调整走开发 GM 的 level 命令");
        var failed = await Post("/api/gold", new { account = "acct", name = "Hero", gold = 999, confirm = true });
        TestCheck.That(failed.StatusCode == HttpStatusCode.BadRequest && (await failed.Content.ReadAsStringAsync()).Contains("金币失败"), "命令失败原样返回错误");
        TestCheck.That((await Post("/api/job", new { account = "acct", name = "Hero", branch = 1, awakening = 0, confirm = true })).IsSuccessStatusCode
            && data.Commands.Contains("account acct grow Hero 1 0"), "转职走 grow 命令");
        TestCheck.That((await Post("/api/currency", new { account = "acct", name = "Hero", key = "cera", op = "add", amount = 10, confirm = true })).IsSuccessStatusCode
            && (await Post("/api/currency", new { account = "acct", name = "Hero", key = "cera", op = "mul", amount = 10, confirm = true })).StatusCode == HttpStatusCode.BadRequest, "货币操作校验");
        TestCheck.That((await http.PostAsync("/api/accounts", null)).StatusCode == HttpStatusCode.Forbidden, "未知写路径同样需要自定义头");

        // 审计：所有写操作都有记录，等级调整带前后摘要，失败也记录。
        var audit = await http.GetFromJsonAsync<JsonElement>("/api/audit?limit=50");
        var rows = audit.EnumerateArray().ToArray();
        TestCheck.That(rows.Any(r => r.GetProperty("action").GetString() == "mail-item" && r.GetProperty("ok").GetBoolean()
            && r.GetProperty("target").GetString() == "acct/Hero" && r.GetProperty("operator").GetString()!.StartsWith("本机 ")), "邮件发放写入审计（含操作者本机）");
        var level = rows.First(r => r.GetProperty("action").GetString() == "level");
        TestCheck.That(level.GetProperty("before").GetString()!.Contains("level=50") && level.GetProperty("after").GetString()!.Contains("level=60"), "等级调整审计含前后值摘要");
        TestCheck.That(rows.Any(r => r.GetProperty("action").GetString() == "gold" && !r.GetProperty("ok").GetBoolean()) &&
            rows.Any(r => r.GetProperty("action").GetString() == "force-return" && !r.GetProperty("ok").GetBoolean()) &&
            rows.Any(r => r.GetProperty("action").GetString() == "mail-gold"), "失败与金币邮件也写审计");
        TestCheck.That(!rows.Any(r => r.GetProperty("action").GetString() == "mail-item" && r.GetProperty("command").GetString()!.Contains("he ro")), "被拒绝的请求不写审计");
        File.AppendAllText(auditPath, "not json\n");
        TestCheck.That(data.Audit.Recent(500).Length == rows.Length, "审计读取跳过损坏行");

        // 额外 SP/TP、任务、账号钱包：转成命令，范围校验，缺确认不执行。
        TestCheck.That((await http.GetFromJsonAsync<JsonElement>("/api/characters/acct/Hero/points")).GetProperty("data").GetProperty("bonusSp").GetInt32() == 5
            && data.Commands.Contains("account acct points Hero"), "额外 SP/TP 只读接口");
        TestCheck.That((await Post("/api/sp", new { account = "acct", name = "Hero", value = 50001, confirm = true })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/tp", new { account = "acct", name = "Hero", value = 20, confirm = true })).IsSuccessStatusCode
            && data.Commands.Contains("account acct tp Hero 20"), "额外 TP 写入走 tp 命令并校验范围");
        foreach (var (path, expected) in new[] { ("add", "quest-add Hero 10100"), ("remove", "quest-del Hero 10100"), ("chain", "quest-chain Hero 10100") })
            TestCheck.That((await Post("/api/quests/" + path, new { account = "acct", name = "Hero", quest = 10100, confirm = true })).IsSuccessStatusCode
                && data.Commands.Contains("account acct " + expected), "任务 " + path);
        TestCheck.That((await Post("/api/quests/batch", new { account = "acct", name = "Hero", quests = new[] { 1, 2, 3 }, confirm = true })).IsSuccessStatusCode
            && data.Commands.Contains("account acct quest-batch Hero 1,2,3")
            && (await Post("/api/quests/batch", new { account = "acct", name = "Hero", quests = new[] { 0 }, confirm = true })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/quests/batch", new { account = "acct", name = "Hero", quests = Enumerable.Range(1, 3001).ToArray(), confirm = true })).StatusCode == HttpStatusCode.BadRequest, "批量任务与上限");
        TestCheck.That((await Post("/api/quests/clear", new { account = "acct", name = "Hero", confirm = true })).IsSuccessStatusCode
            && (await Post("/api/quests/clear", new { account = "acct", name = "Hero" })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/quests/add", new { account = "acct", name = "Hero", quest = 0, confirm = true })).StatusCode == HttpStatusCode.BadRequest, "清空任务需确认，非法编号拒绝");
        TestCheck.That((await Post("/api/account-currency", new { account = "acct", key = "cera", op = "add", amount = 5, confirm = true })).StatusCode == HttpStatusCode.BadRequest, "非账号级货币（假数据钱包无 currencies）拒绝");

        // 删除/清空/直接入包：游戏中的角色默认拒绝；returnFirst 才先强制小退再重试，并审计两步。
        data.InGame = true; data.Returned.Clear();
        var refused = await Post("/api/items/delete", new { account = "acct", name = "Hero", refs = new[] { "0:3:99" }, confirm = true });
        TestCheck.That(refused.StatusCode == HttpStatusCode.BadRequest && (await refused.Content.ReadAsStringAsync()).Contains("游戏中") && data.Returned.Count == 0, "游戏中的角色默认拒绝删除物品且不小退");
        int returnedBefore = data.Returned.Count;
        TestCheck.That((await Post("/api/items/delete", new { account = "acct", name = "Hero", refs = new[] { "0:3:99" }, confirm = true })).StatusCode == HttpStatusCode.BadRequest
            && data.Returned.Count == returnedBefore, "未选 returnFirst 不会小退");
        var done = await Post("/api/items/delete", new { account = "acct", name = "Hero", refs = new[] { "0:3:99" }, returnFirst = true, confirm = true });
        TestCheck.That(done.IsSuccessStatusCode && data.Returned.Count == returnedBefore + 1 && data.Commands.Contains("account acct item-delete Hero 0:3:99"), "returnFirst 先小退再删除");
        TestCheck.That((await Post("/api/items/delete", new { account = "acct", name = "Hero", refs = new[] { "0:3:99;x" }, returnFirst = true, confirm = true })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/items/clear", new { account = "acct", name = "Hero", container = 300, confirm = true })).StatusCode == HttpStatusCode.BadRequest, "物品引用与容器校验");
        TestCheck.That((await Post("/api/items/clear", new { account = "acct", name = "Hero", container = 0, confirm = true })).IsSuccessStatusCode
            && data.Commands.Contains("account acct item-clear Hero 0")
            && (await Post("/api/mails/delete", new { account = "acct", name = "Hero", all = true, confirm = true })).IsSuccessStatusCode
            && data.Commands.Contains("account acct mail-delete Hero all")
            && (await Post("/api/mails/delete", new { account = "acct", name = "Hero", ids = new uint[] { 4, 5 }, confirm = true })).IsSuccessStatusCode
            && data.Commands.Contains("account acct mail-delete Hero 4,5"), "清空容器与删邮件（角色已回到选角）");
        data.InGame = true;
        TestCheck.That((await Post("/api/direct/item", new { account = "acct", name = "Hero", itemId = 7, count = 1, upgrade = 0, refine = 0, confirm = true })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/direct/item", new { account = "acct", name = "Hero", itemId = 7, count = 1, upgrade = 0, refine = 0, returnFirst = true, confirm = true })).IsSuccessStatusCode
            && data.Commands.Contains("account acct grant Hero 7 1 0 0 direct"), "直接入包对在线角色同样要求先小退");
        data.InGame = true;
        TestCheck.That((await Post("/api/direct/set", new { account = "acct", name = "Hero", setIndex = 2, upgrade = 0, refine = 0, confirm = true })).StatusCode == HttpStatusCode.BadRequest, "直接发套装同样要求先小退");

        // 称号簿/属性/金库/高级筛选：只读走命令、写入严格校验；称号完成对在线角色同样要求先小退。
        TestCheck.That((await http.GetAsync("/api/characters/acct/Hero/titles?q=general")).IsSuccessStatusCode && data.Commands.Contains("account acct title-list Hero general")
            && (await http.GetAsync("/api/characters/acct/Hero/stats")).IsSuccessStatusCode && data.Commands.Contains("account acct stats Hero"), "称号簿与属性只读接口");
        data.InGame = true;
        TestCheck.That((await Post("/api/titles/complete", new { account = "acct", name = "Hero", spec = "book", confirm = true })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/titles/complete", new { account = "acct", name = "Hero", spec = "book:general", returnFirst = true, confirm = true })).IsSuccessStatusCode
            && data.Commands.Contains("account acct title-complete Hero book:general"), "称号完成对在线角色要求先小退");
        TestCheck.That((await Post("/api/titles/complete", new { account = "acct", name = "Hero", spec = "book:nope", confirm = true })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/titles/complete", new { account = "acct", name = "Hero", spec = "1,2;3", confirm = true })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/titles/complete", new { account = "acct", name = "Hero", spec = "all" })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/titles/complete", new { account = "acct", name = "Hero", spec = "100,200", confirm = true })).IsSuccessStatusCode, "成就范围校验与确认");
        TestCheck.That((await Post("/api/vault-gold", new { account = "acct", name = "Hero", op = "set", amount = 1000, confirm = true })).IsSuccessStatusCode
            && data.Commands.Contains("account acct currency Hero vault-gold set 1000")
            && (await Post("/api/vault-gold", new { account = "acct", name = "Hero", op = "mul", amount = 1, confirm = true })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/vault-gold", new { account = "acct", name = "Hero", op = "set", amount = 2147483648L, confirm = true })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/vault-gold", new { account = "acct", name = "Hero", op = "set", amount = 1 })).StatusCode == HttpStatusCode.BadRequest, "金库金币写入与校验");
        TestCheck.That((await http.GetAsync("/api/items/filter?stat=physical_attack&period=abs&skillJob=swordman&q=sword")).IsSuccessStatusCode
            && data.Commands.Contains("item-filter 0/50 0 0 * * physical_attack abs swordman sword")
            && (await http.GetAsync("/api/items/filter?stat=a;b")).StatusCode == HttpStatusCode.BadRequest
            && (await http.GetAsync("/api/items/filter?period=sometimes")).StatusCode == HttpStatusCode.BadRequest
            && (await http.GetAsync("/api/items/stat-keys")).IsSuccessStatusCode, "高级筛选接口转成命令并校验参数");

        // 邮件发放成功后提醒在线角色；失败不提醒。
        TestCheck.That(data.Notified.Count >= 3 && data.Notified.All(n => n == "acct/Hero"), "邮件发放成功后触发新邮件提醒");
        int notified = data.Notified.Count;
        await Post("/api/mail/gold", new { account = "acct", name = "Hero", gold = 0, confirm = true });
        TestCheck.That(data.Notified.Count == notified, "校验失败的邮件不提醒");
        var trail = (await http.GetFromJsonAsync<JsonElement>("/api/audit?limit=200")).EnumerateArray().Select(r => r.GetProperty("action").GetString()).ToArray();
        TestCheck.That(trail.Contains("item-delete") && trail.Contains("mail-delete") && trail.Contains("extra-tp") && trail.Contains("quest-batch") && trail.Contains("direct-item"), "新增写操作都写审计");

        TestCheck.That((await Post("/api/characters/rename", new { account = "acct", name = "Hero", newName = "Hero2", confirm = true })).IsSuccessStatusCode
            && data.Commands.Contains("account acct rename Hero Hero2")
            && (await Post("/api/characters/rename", new { account = "acct", name = "Hero", newName = "a b", confirm = true })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/characters/rename", new { account = "acct", name = "Hero", newName = "Hero2" })).StatusCode == HttpStatusCode.BadRequest, "改名接口校验与命令");
        TestCheck.That((await Post("/api/characters/delete", new { account = "acct", name = "Hero", confirmName = "Nope", confirm = true })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/characters/delete", new { account = "acct", name = "Hero", confirmName = "Hero", confirm = true })).IsSuccessStatusCode
            && data.Commands.Contains("account acct char-delete Hero Hero"), "删除角色须确认名一致");
        TestCheck.That((await http.GetAsync("/api/events")).IsSuccessStatusCode
            && (await Post("/api/events", new { id = 64, op = "on", confirm = true })).IsSuccessStatusCode && data.Commands.Contains("event on 64")
            && (await Post("/api/events", new { id = 64, op = "schedule", start = "2026-10-20T00:00:00+08:00", end = "2026-10-27T00:00:00+08:00", confirm = true })).IsSuccessStatusCode
            && (await Post("/api/events", new { id = 64, op = "schedule", start = "x y", end = "z", confirm = true })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/events", new { id = 1, op = "on", confirm = true })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/events", new { id = 64, op = "on" })).StatusCode == HttpStatusCode.BadRequest, "活动开关读写与校验");
        TestCheck.That((await Post("/api/quick-items", new { items = new[] { new { id = 7, label = "测试", count = 2, upgrade = 3, refine = 0 } }, confirm = true })).IsSuccessStatusCode
            && (await http.GetFromJsonAsync<JsonElement>("/api/quick-items")).GetProperty("data")[0].GetProperty("Id").GetInt32() == 7
            && (await Post("/api/quick-items", new { items = new[] { new { id = 0, label = "x", count = 1, upgrade = 0, refine = 0 } }, confirm = true })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/quick-items", new { items = new[] { new { id = 1, label = "x", count = 1, upgrade = 0, refine = 0 } } })).StatusCode == HttpStatusCode.BadRequest, "快捷物品读写与校验");
        var icon = await http.GetAsync("/api/items/7/icon");
        TestCheck.That(icon.StatusCode == HttpStatusCode.OK && icon.Content.Headers.ContentType?.MediaType == "image/png" && (await icon.Content.ReadAsByteArrayAsync()).Length == 4
            && (await http.GetAsync("/api/items/8/icon")).StatusCode == HttpStatusCode.NoContent, "图标接口：有图返回 PNG，无图 204 由前端显示占位");
        TestCheck.That((await http.GetFromJsonAsync<JsonElement>("/api/items/7/preview")).GetProperty("data").GetProperty("stats")[0].GetString() == "力量 +3"
            && (await http.GetAsync("/api/items/8/preview")).StatusCode == HttpStatusCode.NotFound, "预览接口");

        // 第三轮接口：技能列表、经验表、按经验设等级、在线称号、断线、审计筛选与导出。
        TestCheck.That((await http.GetAsync("/api/characters/acct/Hero/skill-list")).IsSuccessStatusCode && data.Commands.Contains("account acct skill-list Hero")
            && (await http.GetAsync("/api/characters/acct/bad%20name/skill-list")).StatusCode == HttpStatusCode.BadRequest, "skill-list 只读接口转发并校验角色名");
        var expTable = await http.GetFromJsonAsync<JsonElement>("/api/exp-table");
        TestCheck.That(expTable.GetProperty("data").GetProperty("levels").GetArrayLength() == 3 && data.Commands.Contains("exp-table"), "exp-table 接口转发");
        int beforeExp = data.Commands.Count;
        TestCheck.That((await Post("/api/exp", new { account = "acct", name = "Hero", exp = 1500L, confirm = true })).IsSuccessStatusCode
            && data.Commands.Contains("account acct level Hero 2")
            && (await Post("/api/exp", new { account = "acct", name = "Hero", exp = 3000L, confirm = true })).IsSuccessStatusCode
            && data.Commands.Contains("account acct level Hero 3")
            && (await Post("/api/exp", new { account = "acct", name = "Hero", exp = 999L, confirm = true })).IsSuccessStatusCode
            && data.Commands.Contains("account acct level Hero 1"), "/api/exp 按经验表换算等级（边界）");
        int afterExp = data.Commands.Count;
        TestCheck.That((await Post("/api/exp", new { account = "acct", name = "Hero", exp = -1L, confirm = true })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/exp", new { account = "acct", name = "Hero", exp = 6_000_000_001L, confirm = true })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/exp", new { account = "acct", name = "Hero", exp = 10L })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/exp", new { account = "acct", name = "Hero", exp = 10L, confirm = true }, header: false)).StatusCode == HttpStatusCode.Forbidden
            && data.Commands.Count == afterExp, "/api/exp 拒绝越界、缺确认、缺请求头");
        data.InGame = false;
        TestCheck.That((await Post("/api/titles/complete", new { account = "acct", name = "Hero", spec = "book:general", confirm = true })).IsSuccessStatusCode
            && data.Returned.Count(r => r == "acct/Hero") == data.Returned.Count, "称号完成（角色不在游戏中）无需强制小退");
        TestCheck.That((await Post("/api/disconnect", new { account = "acct", confirm = true })).IsSuccessStatusCode && data.Disconnected.SequenceEqual(["acct"])
            && (await Post("/api/disconnect", new { account = "ghost", confirm = true })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/disconnect", new { account = "acct" })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/disconnect", new { account = "a b", confirm = true })).StatusCode == HttpStatusCode.BadRequest
            && (await Post("/api/disconnect", new { account = "acct", confirm = true }, header: false)).StatusCode == HttpStatusCode.Forbidden
            && (await Post("/api/disconnect", new { account = "acct", confirm = true }, origin: "http://evil.example")).StatusCode == HttpStatusCode.Forbidden
            && data.Disconnected.Count == 2, "断线接口：成功、未知账号、缺确认、非法账号、缺头与跨站");
        data.Audit.Append(new(DateTimeOffset.Now, "gm", "probe", "acct/Hero", "=cmd|' /c calc'!A1", true, "", "", "line1\nline2, \"q\""));
        TestCheck.That((await http.GetFromJsonAsync<JsonElement>("/api/audit?account=acct&action=disconnect&ok=0")).GetArrayLength() == 0
            && (await http.GetFromJsonAsync<JsonElement>("/api/audit?account=acct&action=disconnect&ok=1")).GetArrayLength() == 1, "审计组合筛选（账号+动作+结果）");
        var byAction = await http.GetFromJsonAsync<JsonElement>("/api/audit?action=disconnect");
        TestCheck.That(byAction.GetArrayLength() == 2 && byAction.EnumerateArray().All(r => r.GetProperty("action").GetString() == "disconnect"), "审计按动作筛选");
        var byAccount = await http.GetFromJsonAsync<JsonElement>("/api/audit?account=ghost&action=disconnect&limit=50");
        TestCheck.That(byAccount.GetArrayLength() == 1 && byAccount[0].GetProperty("ok").GetBoolean() == false, "审计按账号筛选，失败操作也有记录");
        var failedOnly = await http.GetFromJsonAsync<JsonElement>("/api/audit?ok=0&limit=500");
        TestCheck.That(failedOnly.GetArrayLength() >= 1 && failedOnly.EnumerateArray().All(r => !r.GetProperty("ok").GetBoolean()), "审计按结果筛选");
        TestCheck.That((await http.GetFromJsonAsync<JsonElement>("/api/audit?from=2099-01-01")).GetArrayLength() == 0
            && (await http.GetFromJsonAsync<JsonElement>("/api/audit?from=2000-01-01&to=2099-01-01")).GetArrayLength() > 0
            && (await http.GetFromJsonAsync<JsonElement>($"/api/audit?action=probe&from={DateTime.Now:yyyy-MM-dd}&to={DateTime.Now:yyyy-MM-dd}")).GetArrayLength() == 1, "审计时间范围（仅日期的 to 含当天）");
        TestCheck.That((await http.GetAsync("/api/audit?from=notadate")).StatusCode == HttpStatusCode.BadRequest
            && (await http.GetAsync("/api/audit?to=x")).StatusCode == HttpStatusCode.BadRequest
            && (await http.GetAsync("/api/audit?account=a%20b")).StatusCode == HttpStatusCode.BadRequest
            && (await http.GetAsync("/api/audit?action=a%0Ab")).StatusCode == HttpStatusCode.BadRequest, "审计非法筛选参数被拒绝");
        var csv = await http.GetAsync("/api/audit?action=probe&format=csv");
        string csvText = await csv.Content.ReadAsStringAsync();
        TestCheck.That(csv.IsSuccessStatusCode && csv.Content.Headers.ContentType?.MediaType == "text/csv" && csv.Content.Headers.ContentDisposition?.FileName == "gm-audit.csv"
            && csvText.Contains("\"'=cmd|") && !csvText.Contains(",\"=cmd") && csvText.Contains("\"line1\nline2, \"\"q\"\"\""), "审计 CSV 导出：防公式注入并正确转义");
        TestCheck.That(!GmAudit.ToCsv([new(DateTimeOffset.Now, "gm", "x", " =1", "@a", true, "", "", "")]).Contains("\" =1"), "CSV 前导空白后的公式也被中和");

        stop.Cancel();
        await server.WaitAsync(TimeSpan.FromSeconds(10));
        Directory.Delete(directory, true);
        Console.WriteLine($"PASS {TestCheck.Count - before} GM 网页站点接口、写操作校验与审计断言");
    }
}