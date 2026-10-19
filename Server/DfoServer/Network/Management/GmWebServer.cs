using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DfoServer.Network.Management;

internal sealed record GmWebStatus(int Pid, DateTimeOffset StartedAt, int ChannelPort, int GamePort, int GmPort, int GmWebPort,
    string PvfPath, string DataDirectory, int Online);
internal sealed record GmWebAccount(string Account, bool Disabled, long Revision, int Characters);
internal sealed record GmWebCharacter(int Id, int Slot, string Name, int Job, int GrowType, int Level, long Gold, long Revision);

// GM 站点读取服务进程内数据的窄接口；由 ServerRuntime 用同一份已加载资源与存档实现。
internal interface IGmWebData
{
    GmWebStatus Status();
    OnlineCharacterView[] Online();
    GmWebAccount[] Accounts();
    GmWebCharacter[]? Characters(string account);
    // 经服务进程内的开发 GM 执行器运行一行命令（与 --gm-port 同一入口，在线角色走原 owner 的在线提交路径）。
    string Command(string line);
    // 让在线角色回到选角界面（小退）；没有在线会话或角色不匹配返回 false。
    Task<bool> ForceReturnAsync(string account, string? name);
    // 断开该账号的在线连接（与重复登录顶号同一断线回调）；没有在线会话返回 false。
    bool Disconnect(string account) => false;
    // 金币邮件：走 MailboxService.Deliver，在线与离线都不需要小退。返回 "OK {json}" 或 "ERR 原因"。
    string DeliverGoldMail(string account, string name, uint gold, string text);
    // 邮件投递后提醒在线角色“新邮件”（只对正在游戏的该角色生效）。
    void NotifyMailArrived(string account, string name);
    // 物品图标（PNG，找不到客户端 ImagePacks2 或该物品无图标时为 null）与脚本预览。
    // 快捷物品清单（存档目录 gm-quick-items.json，JSON 数组）。
    string ReadQuickItems();
    void WriteQuickItems(string json);
    byte[]? ItemIcon(int id);
    object? ItemPreview(int id);
    GmAudit Audit { get; }
}

// 嵌入服务进程的 GM 站点（Kestrel Minimal API）。只绑定 127.0.0.1，并校验 Host 头以防 DNS 重绑定；
// 写接口须带 X-GM-Request 头和 confirm=true，Origin 若存在必须同源，每次写操作写审计。
internal static partial class GmWebServer
{
    public static async Task RunAsync(int port, IGmWebData data, CancellationToken stop)
    {
        if (port is < 1 or > 65535)
            throw new InvalidDataException("Invalid GM web port");
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            // 任务“全部完成”会一次提交当前筛选的编号列表，3000 个编号的 JSON 超过 16KB。
            kestrel.Limits.MaxRequestBodySize = 128 * 1024;
            kestrel.Listen(IPAddress.Loopback, port, listen => listen.Protocols = HttpProtocols.Http1);
        });
        await using var app = builder.Build();
        Map(app, data);
        await app.StartAsync(stop);
        try { await Task.Delay(Timeout.Infinite, stop); }
        catch (OperationCanceledException) { }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    static readonly Regex Token = new(@"^[^\s\p{C}]{1,32}$", RegexOptions.Compiled);
    static readonly Regex Amplify = new(@"^amp:[1-4]:[0-9]{1,10}$", RegexOptions.Compiled);
    static readonly Regex Tag = new(@"^[^\s\p{C}]{1,32}$", RegexOptions.Compiled);

    internal static void Map(WebApplication app, IGmWebData data)
    {
        app.Use(async (context, next) =>
        {
            string host = context.Request.Host.Host;
            if (host != "127.0.0.1" && !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                // 防跨站请求：自定义头无法由普通跨站表单带出；Origin 存在时必须与本机 Host 同源。
                string? origin = context.Request.Headers.Origin;
                if (context.Request.Headers["X-GM-Request"] != "1" ||
                    origin is not null && origin != "http://" + context.Request.Host.Value)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }
            await next();
        });
        app.MapGet("/", () => Results.Text(Assets.Text("index.html"), "text/html; charset=utf-8"));
        app.MapGet("/assets/{name}", (string name) => Assets.Find(name) is { } asset
            ? Results.Text(asset.Text, asset.Type) : Results.NotFound());
        app.MapGet("/favicon.ico", () => Results.NoContent());
        app.MapGet("/api/status", () => Results.Json(data.Status()));
        app.MapGet("/api/online", () => Results.Json(data.Online()));
        app.MapGet("/api/accounts", () => Results.Json(data.Accounts()));
        app.MapGet("/api/accounts/{account}/characters", (string account) =>
            data.Characters(account) is { } rows ? Results.Json(rows) : Results.NotFound(new { error = "账号不存在" }));
        MapReads(app, data);
        MapWrites(app, data);
        MapOperations(app, data);
    }

    // 只读详情与物品库：全部是已有 GM 查询命令的 JSON 结果，不改库。
    static void MapReads(WebApplication app, IGmWebData data)
    {
        app.MapGet("/api/characters/{account}/{name}/{section}", (string account, string name, string section, string? q, string? category, string? status, int? page, int? pageSize, string? job) =>
        {
            if (!Token.IsMatch(account) || !Token.IsMatch(name)) return Bad("账号或角色名无效");
            string? command = section switch
            {
                "inventory" => $"account {account} items {name}",
                "mails" => $"account {account} mails {name}",
                "wallet" => $"account {account} wallet {name}",
                "summary" => $"account {account} show {name}",
                "quests" => $"account {account} quests {name}",
                "skills" => $"account {account} skills {name}",
                "stats" => $"account {account} stats {name}",
                "skill-list" => $"account {account} skill-list {name}",
                "titles" when q is null || Keyword(q) => $"account {account} title-list {name} {q}".TrimEnd(),
                "quest-search" when (q is null || Keyword(q) && !Regex.IsMatch(q, "(^| )(cat|st|job|page|size):")) && (category is null || category == "*" || Regex.IsMatch(category, "^[a-z]{1,16}$"))
                    && (status is null || status is "*" or "todo" or "active" or "done") && (job is null || job is "mine" or "any") =>
                    $"account {account} quest-search {name} cat:{(string.IsNullOrEmpty(category) ? "*" : category)} st:{(string.IsNullOrEmpty(status) ? "*" : status)} job:{(job ?? "mine")} page:{Math.Clamp(page ?? 1, 1, 100000)} size:{Math.Clamp(pageSize ?? 50, 1, 500)} {q}".TrimEnd(),
                _ => null
            };
            return command is null ? Results.NotFound(new { error = "未知分区" }) : Reply(data.Command(command));
        });
        app.MapGet("/api/exp-table", () => Reply(data.Command("exp-table")));
        app.MapGet("/api/debug", () => Results.Json(new { ok = true, data = DebugState(data) }));
        app.MapPost("/api/disconnect", (HttpContext http, KickBody b) =>
        {
            if (!Token.IsMatch(b.Account)) return Bad("账号无效");
            if (b.Confirm != true) return Bad("需要二次确认");
            bool ok = data.Disconnect(b.Account);
            string result = ok ? "OK 已断开该账号的连接" : "ERR 账号不在线";
            data.Audit.Append(new(DateTimeOffset.Now, Operator(http), "disconnect", b.Account + "/*", "disconnect " + b.Account, ok, "", "", result));
            return Reply(result);
        });
        app.MapGet("/api/items/categories", () => Reply(data.Command("item-categories")));
        app.MapGet("/api/items/search", (int? offset, int? page, int? pageSize, int? min, int? max, string? rarity, string? tag, string? job, string? grow, string? q) =>
        {
            if (rarity is not null && rarity != "*" && !Regex.IsMatch(rarity, "^[0-9]{1,3}$")) return Bad("稀有度无效");
            if (tag is not null && tag != "*" && !Regex.IsMatch(tag, @"^[^\p{C}]{1,80}$")) return Bad("分类无效");
            if (job is not null && job != "*" && !Regex.IsMatch(job, "^([0-9]|1[0-5])$")) return Bad("职业无效");
            if (grow is not null && grow != "*" && !Regex.IsMatch(grow, "^([0-9]|1[0-5])$")) return Bad("分支无效");
            if (q is not null && !Keyword(q)) return Bad("关键字无效");
            string tagToken = string.IsNullOrEmpty(tag) || tag == "*" ? "*" : tag.Replace(' ', '_');
            string jobToken = (string.IsNullOrEmpty(job) || job == "*" ? "" : " job:" + job) + (string.IsNullOrEmpty(grow) || grow == "*" ? "" : " grow:" + grow);
            return Reply(data.Command($"item-search {PageToken(offset, page, pageSize)} {Math.Clamp(min ?? 0, 0, 255)} {Math.Clamp(max ?? 0, 0, 255)} " +
                $"{(string.IsNullOrEmpty(rarity) ? "*" : rarity)} {tagToken}{jobToken} {q}".TrimEnd()));
        });
        app.MapGet("/api/items/filter", (int? offset, int? page, int? pageSize, int? min, int? max, string? rarity, string? tag, string? stat, string? period, string? skillJob, string? job, string? grow, string? q) =>
        {
            if (rarity is not null && rarity != "*" && !Regex.IsMatch(rarity, "^[0-9]{1,3}$")) return Bad("稀有度无效");
            if (tag is not null && tag != "*" && !Regex.IsMatch(tag, @"^[^\p{C}]{1,80}$")) return Bad("分类无效");
            if (stat is not null && stat != "*" && !Regex.IsMatch(stat, "^[a-z_ ]{1,40}$")) return Bad("词条无效");
            if (period is not null && period is not ("*" or "none" or "rel" or "abs" or "any")) return Bad("期限无效");
            if (skillJob is not null && skillJob != "*" && !Regex.IsMatch(skillJob, "^[a-z_ ]{1,20}$")) return Bad("技能职业无效");
            if (job is not null && job != "*" && !Regex.IsMatch(job, "^([0-9]|1[0-5])$")) return Bad("职业无效");
            if (grow is not null && grow != "*" && !Regex.IsMatch(grow, "^([0-9]|1[0-5])$")) return Bad("分支无效");
            if (q is not null && !Keyword(q)) return Bad("关键字无效");
            string S(string? value, string fallback = "*") => string.IsNullOrEmpty(value) ? fallback : value.Replace(' ', '_');
            string jobToken = (string.IsNullOrEmpty(job) || job == "*" ? "" : " job:" + job) + (string.IsNullOrEmpty(grow) || grow == "*" ? "" : " grow:" + grow);
            return Reply(data.Command($"item-filter {PageToken(offset, page, pageSize)} {Math.Clamp(min ?? 0, 0, 255)} {Math.Clamp(max ?? 0, 0, 255)} " +
                $"{S(rarity)} {S(tag)} {S(stat)} {S(period)} {S(skillJob)}{jobToken} {q}".TrimEnd()));
        });
        app.MapGet("/api/items/stat-keys", () =>
        {
            // 词条命中数来自词条索引（未就绪时 ready=false，只给词条名；前端稍后重试即可显示命中数）。
            var counts = new Dictionary<string, int>(); bool ready = false;
            try
            {
                using var document = JsonDocument.Parse(data.Command("item-stat-counts") is { } text && text.StartsWith("OK ", StringComparison.Ordinal) ? text[3..] : "{}");
                if (document.RootElement.TryGetProperty("ready", out var flag) && flag.ValueKind == JsonValueKind.True)
                {
                    ready = true;
                    foreach (var entry in document.RootElement.GetProperty("counts").EnumerateObject()) counts[entry.Name] = entry.Value.GetInt32();
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
            return Results.Text(System.Text.Json.JsonSerializer.Serialize(new
            {
                ok = true, ready,
                data = GmItemStatSpecs.Table.Values.GroupBy(spec => spec.Key).Select(group => new
                {
                    key = group.Key.Replace(' ', '_'), label = group.First().Label, count = ready ? counts.GetValueOrDefault(group.Key.Replace(' ', '_')) : (int?)null
                }).OrderBy(row => row.label)
            }), "application/json");
        });
        app.MapGet("/api/index-status", () => Reply(data.Command("index-status")));
        app.MapGet("/api/items/{id:int}", (int id) => Reply(data.Command($"item-info {id}")));
        app.MapGet("/api/items/{id:int}/icon", (int id, HttpContext http) =>
        {
            byte[]? png = data.ItemIcon(id);
            if (png is null) return Results.NoContent();
            http.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.File(png, "image/png");
        });
        app.MapGet("/api/items/{id:int}/preview", (int id) =>
            data.ItemPreview(id) is { } preview ? Results.Json(new { ok = true, data = preview }) : Results.Json(new { ok = false, error = "PVF 中没有这个物品" }, statusCode: 404));
        app.MapGet("/api/audit", (HttpContext http, int? limit, int? page, int? pageSize, string? account, string? action, string? from, string? to, string? ok, string? format) =>
        {
            if ((account is not null && !Token.IsMatch(account)) || (action is not null && !Token.IsMatch(action))) return Bad("过滤参数含空白或控制字符");
            DateTimeOffset? start = null, end = null;
            if (from is not null) { if (!DateTimeOffset.TryParse(from, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var f)) return Bad("from 时间格式无效"); start = f; }
            if (to is not null) { if (!DateTimeOffset.TryParse(to, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var t)) return Bad("to 时间格式无效"); end = t; if (System.Text.RegularExpressions.Regex.IsMatch(to, "^[0-9]{4}-[0-9]{2}-[0-9]{2}$")) end = t.AddDays(1).AddTicks(-1); }
            bool? success = ok is "1" or "true" ? true : ok is "0" or "false" ? false : null;
            var all = data.Audit.Query(page is null ? limit ?? 200 : 5000, account, action, start, end, success);
            var rows = all;
            if (page is { } pageNo)
            {
                // 分页：总数放在 X-Total-Count 响应头里，响应体仍是行数组（兼容旧调用）。
                int size = Math.Clamp(pageSize ?? 50, 1, 500);
                http.Response.Headers["X-Total-Count"] = all.Length.ToString();
                rows = [.. all.Skip((Math.Clamp(pageNo, 1, 100000) - 1) * size).Take(size)];
            }
            if (string.Equals(format, "csv", StringComparison.Ordinal))
                return Results.File(System.Text.Encoding.UTF8.GetBytes(GmAudit.ToCsv(rows)), "text/csv; charset=utf-8", "gm-audit.csv");
            return Results.Json(rows);
        });
    }

    // 写操作：邮件发放、强制小退、钱包/等级/转职（后三者由开发 GM 的在线提交或租约路径执行）。
    static void MapWrites(WebApplication app, IGmWebData data)
    {
        app.MapPost("/api/mail/item", (HttpContext http, MailItemBody b) =>
        {
            if (Invalid(b.Account, b.Name, b.Confirm) is { } bad) return bad;
            if (b.ItemId <= 0 || b.Count is < 1 or > 99999 || b.Upgrade is < 0 or > 31 || b.Refine is < 0 or > 99 ||
                b.Amp is not null && !Amplify.IsMatch(b.Amp) && b.Amp != "amp:0:0" || b.Ability is < 0 or > 65535) return Bad("物品参数无效");
            if (b.ClearPeriod == true && b.Expire is not null) return Bad("期限不能同时改为不过期和指定时间");
            if (b.Expire is < 1) return Bad("到期时间无效");
            return Run(http, data, "mail-item", b.Account, b.Name, $"account {b.Account} grant {b.Name} {b.ItemId} {b.Count} {b.Upgrade} {b.Refine} mail{(b.Amp is null ? "" : " " + b.Amp)}{(b.Unseal == true ? " seal:open" : "")}{(b.Ability is null ? "" : " ability:" + b.Ability)}{(b.ClearPeriod == true ? " period:off" : "")}{(b.Expire is null ? "" : " expire:" + b.Expire)}", null);
        });
        app.MapPost("/api/mail/set", (HttpContext http, MailSetBody b) =>
        {
            if (Invalid(b.Account, b.Name, b.Confirm) is { } bad) return bad;
            if (b.SetIndex < 0 || b.Upgrade is < 0 or > 31 || b.Refine is < 0 or > 99) return Bad("套装参数无效");
            return Run(http, data, "mail-set", b.Account, b.Name, $"account {b.Account} set-grant {b.Name} {b.SetIndex} {b.Upgrade} {b.Refine} mail", null);
        });
        app.MapPost("/api/mail/gold", (HttpContext http, MailGoldBody b) =>
        {
            if (Invalid(b.Account, b.Name, b.Confirm) is { } bad) return bad;
            if (b.Gold is < 1 or > 2_000_000_000) return Bad("金币须在 1–2000000000");
            string text = string.IsNullOrWhiteSpace(b.Text) ? "开发GM发放金币" : b.Text.Trim();
            if (text.Length > 60 || text.Any(char.IsControl)) return Bad("邮件文字无效");
            return Run(http, data, "mail-gold", b.Account, b.Name, $"mail-gold {b.Account} {b.Name} {b.Gold}", null,
                () => data.DeliverGoldMail(b.Account, b.Name, (uint)b.Gold, text));
        });
        app.MapPost("/api/kick", async (HttpContext http, KickBody b) =>
        {
            if (!Token.IsMatch(b.Account) || b.Name is not null && !Token.IsMatch(b.Name)) return Bad("账号或角色名无效");
            if (b.Confirm != true) return Bad("需要二次确认");
            bool ok = await data.ForceReturnAsync(b.Account, b.Name);
            string result = ok ? "OK 已让角色回到选角" : "ERR 账号不在线，或指定角色不在游戏中";
            data.Audit.Append(new(DateTimeOffset.Now, Operator(http), "force-return", b.Account + "/" + (b.Name ?? "*"), "force-return",
                ok, "", "", result));
            return Reply(result);
        });
        app.MapPost("/api/gold", (HttpContext http, GoldBody b) =>
        {
            if (Invalid(b.Account, b.Name, b.Confirm) is { } bad) return bad;
            if (b.Gold is < 0 or > int.MaxValue) return Bad("金币须在 0–2147483647");
            return Run(http, data, "gold", b.Account, b.Name, $"account {b.Account} gold {b.Name} {b.Gold}", $"account {b.Account} wallet {b.Name}");
        });
        app.MapPost("/api/currency", (HttpContext http, CurrencyBody b) =>
        {
            if (Invalid(b.Account, b.Name, b.Confirm) is { } bad) return bad;
            if (!Token.IsMatch(b.Key) || b.Op is not ("set" or "add" or "sub") || b.Amount is < 0 or > int.MaxValue) return Bad("货币参数无效");
            return Run(http, data, "currency", b.Account, b.Name, $"account {b.Account} currency {b.Name} {b.Key} {b.Op} {b.Amount}", $"account {b.Account} wallet {b.Name}");
        });
        app.MapPost("/api/level", (HttpContext http, LevelBody b) =>
        {
            if (Invalid(b.Account, b.Name, b.Confirm) is { } bad) return bad;
            if (b.Level is < 1 or > 95) return Bad("等级须在 1–95");
            return Run(http, data, "level", b.Account, b.Name, $"account {b.Account} level {b.Name} {b.Level}", $"account {b.Account} show {b.Name}");
        });
        app.MapPost("/api/exp", (HttpContext http, ExpBody b) =>
        {
            if (Invalid(b.Account, b.Name, b.Confirm) is { } bad) return bad;
            if (b.Exp is < 0 or > 6_000_000_000L) return Bad("累计经验须在 0–6000000000");
            string table = data.Command("exp-table");
            if (!table.StartsWith("OK ", StringComparison.Ordinal)) return Reply(table);
            using var document = System.Text.Json.JsonDocument.Parse(table[3..]);
            int level = 1;
            foreach (var row in document.RootElement.GetProperty("levels").EnumerateArray())
                if (row.GetProperty("start").GetUInt64() <= (ulong)b.Exp) level = row.GetProperty("level").GetInt32();
            return Run(http, data, "level", b.Account, b.Name, $"account {b.Account} level {b.Name} {level}", $"account {b.Account} show {b.Name}");
        });
        app.MapPost("/api/job", (HttpContext http, JobBody b) =>
        {
            if (Invalid(b.Account, b.Name, b.Confirm) is { } bad) return bad;
            if (b.Branch is < 0 or > 5 || b.Awakening is < 0 or > 2) return Bad("分支或觉醒无效");
            return Run(http, data, "job", b.Account, b.Name, $"account {b.Account} grow {b.Name} {b.Branch} {b.Awakening}", $"account {b.Account} show {b.Name}");
        });
        app.MapGet("/api/characters/{account}/{name}/set/{index:int}", (string account, string name, int index) =>
        {
            if (!Token.IsMatch(account) || !Token.IsMatch(name) || index <= 0) return Bad("账号、角色或套装编号无效");
            return Reply(data.Command($"account {account} set-members {name} {index}"));
        });
        app.MapGet("/api/characters/{account}/{name}/growth", (string account, string name) =>
            !Token.IsMatch(account) || !Token.IsMatch(name) ? Bad("账号或角色名无效") : Reply(data.Command($"account {account} grow-options {name}")));
        app.MapPost("/api/debug", (HttpContext http, DebugBody b) =>
        {
            if (b.Confirm != true) return Bad("需要二次确认");
            if (b.Value is not ("on" or "off")) return Bad("开关须为 on 或 off");
            if (b.Key is not ("infinite-fatigue" or "cash-shop-expiration" or "cash-shop-usable-period" or "pc-room")) return Bad("未知调试开关");
            return Run(http, data, "debug", "-", b.Key, $"{b.Key} {b.Value}", b.Key);
        });
    }

    static Dictionary<string, bool?> DebugState(IGmWebData data)
    {
        bool? Flag(string command, string property)
        {
            string result = data.Command(command);
            if (!result.StartsWith("OK ", StringComparison.Ordinal)) return null;
            try
            {
                using var document = JsonDocument.Parse(result[3..]);
                return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? value.GetBoolean() : null;
            }
            catch (JsonException) { return null; }
        }
        return new()
        {
            ["infiniteFatigue"] = Flag("infinite-fatigue", "infiniteFatigue"),
            ["ignoreAbsoluteExpiration"] = Flag("cash-shop-expiration", "ignoreAbsoluteExpiration"),
            ["ignoreUsablePeriod"] = Flag("cash-shop-usable-period", "ignoreUsablePeriod"),
            ["premiumPcRoom"] = Flag("pc-room", "premiumPcRoom")
        };
    }

    internal sealed record MailItemBody(string Account, string Name, int ItemId, int Count, int Upgrade, int Refine, string? Amp, bool? Confirm, bool? Unseal = null, int? Ability = null, bool? ClearPeriod = null, int? Expire = null);
    internal sealed record MailSetBody(string Account, string Name, int SetIndex, int Upgrade, int Refine, bool? Confirm);
    internal sealed record MailGoldBody(string Account, string Name, long Gold, string? Text, bool? Confirm);
    internal sealed record KickBody(string Account, string? Name, bool? Confirm);
    internal sealed record GoldBody(string Account, string Name, long Gold, bool? Confirm);
    internal sealed record CurrencyBody(string Account, string Name, string Key, string Op, long Amount, bool? Confirm);
    internal sealed record LevelBody(string Account, string Name, int Level, bool? Confirm);
    internal sealed record JobBody(string Account, string Name, int Branch, int Awakening, bool? Confirm);
    internal sealed record DebugBody(string Key, string Value, bool? Confirm);

    // 物品分页：page/pageSize（pageSize 1–200，默认 50）；兼容旧的 offset。命令偏移参数写成 "偏移/每页条数"。
    static string PageToken(int? offset, int? page, int? pageSize)
    {
        int size = Math.Clamp(pageSize ?? 50, 1, 200);
        int start = page is { } p ? (Math.Clamp(p, 1, 100000) - 1) * size : Math.Clamp(offset ?? 0, 0, 1_000_000);
        return $"{start}/{size}";
    }

    static bool Keyword(string value) => value.Length <= 40 && value.All(c => !char.IsControl(c));

    static IResult? Invalid(string account, string name, bool? confirm) =>
        !Token.IsMatch(account ?? "") || !Token.IsMatch(name ?? "") ? Bad("账号或角色名无效") :
        confirm != true ? Bad("需要二次确认") : null;

    // 账号租约被占用（并发写同一账号）是可重试的冲突，返回 409 而不是参数错误 400。
    static IResult Bad(string message) => Results.Json(new { ok = false, error = message },
        statusCode: message.Contains("账号正在使用", StringComparison.Ordinal) ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest);

    static string Operator(HttpContext http) => "本机 " + http.Connection.RemoteIpAddress + ":" + http.Connection.RemotePort;

    // 执行写命令并审计：前后摘要取自只读命令（如钱包/概要），结果截断记录。
    static IResult Run(HttpContext http, IGmWebData data, string action, string account, string name, string command,
        string? snapshot, Func<string>? execute = null)
    {
        string before = snapshot is null ? "" : data.Command(snapshot);
        string result = execute is null ? data.Command(command) : execute();
        bool ok = result.StartsWith("OK ", StringComparison.Ordinal);
        string after = ok && snapshot is not null ? data.Command(snapshot) : "";
        if (ok && action.StartsWith("mail-", StringComparison.Ordinal)) data.NotifyMailArrived(account, name);
        data.Audit.Append(new(DateTimeOffset.Now, Operator(http), action, account + "/" + name, command, ok, before, after, result));
        return Reply(result);
    }

    // "OK {json}" 返回 data，"OK 文本" 返回 text，"ERR …" 返回 400。
    internal static IResult Reply(string result, bool returned = false)
    {
        if (result.StartsWith("OK ", StringComparison.Ordinal))
        {
            string body = result[3..];
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    return returned ? Results.Json(new { ok = true, data = document.RootElement.Clone(), returned = true }) : Results.Json(new { ok = true, data = document.RootElement.Clone() });
            }
            catch (JsonException) { }
            return returned ? Results.Json(new { ok = true, text = body, returned = true }) : Results.Json(new { ok = true, text = body });
        }
        return Bad(result.StartsWith("ERR ", StringComparison.Ordinal) ? result[4..] : result);
    }

    // 内嵌静态资源：只允许白名单文件名。
    static class Assets
    {
        static readonly Regex Name = new("^[a-z0-9-]+\\.(js|css)$", RegexOptions.Compiled);
        public static string Text(string file)
        {
            using var stream = typeof(GmWebServer).Assembly.GetManifestResourceStream("DfoServer.GmWeb." + file)
                ?? throw new InvalidDataException("程序集缺少内嵌 GM 资源：" + file);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        public static (string Text, string Type)? Find(string file)
        {
            if (!Name.IsMatch(file)) return null;
            try { return (Text(file), file.EndsWith(".css") ? "text/css; charset=utf-8" : "text/javascript; charset=utf-8"); }
            catch (InvalidDataException) { return null; }
        }
    }
}
