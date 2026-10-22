using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DfoServer.Network.Management;

// 其余写操作：额外 SP/TP、任务、物品/容器/邮件删除、直接入包、账号钱包。
// 一律转成开发 GM 命令，由命令层选择在线提交（owner 锁内规划、校验 revision、原子提交并同步在线快照）或账号租约路径。
// 删除物品/清空容器/删邮件/直接入包在命令层拒绝“正在游戏中”的角色：此时只有 returnFirst=true 才会先强制小退再重试，绝不直接改在线角色的库。
internal static partial class GmWebServer
{
    static readonly Regex ItemRef = new(@"^[0-9]{1,3}:[0-9]{1,5}:[0-9]{1,19}$", RegexOptions.Compiled);
    static readonly Regex EventTime = new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.Compiled);
    const string InGame = "角色正在游戏中";

    internal sealed record PointsBody(string Account, string Name, int Value, bool? Confirm);
    internal sealed record QuestBody(string Account, string Name, int Quest, bool? Confirm);
    internal sealed record QuestBatchBody(string Account, string Name, int[]? Quests, bool? Confirm);
    internal sealed record NameBody(string Account, string Name, bool? Confirm);
    internal sealed record DeleteItemsBody(string Account, string Name, string[]? Refs, bool? ReturnFirst, bool? Confirm);
    internal sealed record ClearBody(string Account, string Name, int Container, bool? ReturnFirst, bool? Confirm);
    internal sealed record DeleteMailBody(string Account, string Name, uint[]? Ids, bool? All, bool? ReturnFirst, bool? Confirm);
    internal sealed record DirectItemBody(string Account, string Name, int ItemId, int Count, int Upgrade, int Refine, string? Amp, bool? ReturnFirst, bool? Confirm, bool? Unseal = null, int? Ability = null, bool? ClearPeriod = null, int? Expire = null);
    internal sealed record ItemEditBody(string Account, string Name, int List, int Slot, long Uid, int? Upgrade, int? Refine, string? Amp, int? Ability, int? Count, bool? ClearPeriod, bool? ReturnFirst, bool? Confirm, int? Expire = null);
    internal sealed record DirectSetBody(string Account, string Name, int SetIndex, int Upgrade, int Refine, bool? ReturnFirst, bool? Confirm);
    internal sealed record QuickItem(int Id, string? Label, int Count, int Upgrade, int Refine);
    internal sealed record QuickItemsBody(QuickItem[]? Items, bool? Confirm);
    internal sealed record RenameBody(string Account, string Name, string NewName, bool? ReturnFirst, bool? Confirm);
    internal sealed record DeleteCharacterBody(string Account, string Name, string ConfirmName, bool? ReturnFirst, bool? Confirm);
    internal sealed record EventBody(int Id, string Op, string? Start, string? End, bool? Confirm);
    internal sealed record ExpBody(string Account, string Name, long Exp, bool? Confirm);
    internal sealed record TitleCompleteBody(string Account, string Name, string Spec, bool? Confirm, bool? ReturnFirst);
    internal sealed record VaultGoldBody(string Account, string Name, string Op, long Amount, bool? Confirm);
    internal sealed record AccountCurrencyBody(string Account, string Key, string Op, long Amount, bool? Confirm);
    internal sealed record EquipSlotsBody(string Account, string Name, int Flags, bool? ReturnFirst, bool? Confirm);

    static void MapOperations(WebApplication app, IGmWebData data)
    {
        app.MapGet("/api/characters/{account}/{name}/points", (string account, string name) =>
            !Token.IsMatch(account) || !Token.IsMatch(name) ? Bad("账号或角色名无效") : Reply(data.Command($"account {account} points {name}")));
        app.MapPost("/api/sp", (HttpContext http, PointsBody b) => Points(http, data, "sp", b));
        app.MapPost("/api/tp", (HttpContext http, PointsBody b) => Points(http, data, "tp", b));

        app.MapPost("/api/quests/add", (HttpContext http, QuestBody b) => Quest(http, data, "quest-add", "quest-add", b));
        app.MapPost("/api/quests/remove", (HttpContext http, QuestBody b) => Quest(http, data, "quest-remove", "quest-del", b));
        app.MapPost("/api/quests/chain", (HttpContext http, QuestBody b) => Quest(http, data, "quest-chain", "quest-chain", b));
        app.MapPost("/api/quests/batch", (HttpContext http, QuestBatchBody b) =>
        {
            if (Invalid(b.Account, b.Name, b.Confirm) is { } bad) return bad;
            if (b.Quests is not { Length: >= 1 and <= 3000 } ids || ids.Any(id => id is < 1 or > 29999)) return Bad("任务列表须为 1–3000 个 1–29999 的编号");
            return Run(http, data, "quest-batch", b.Account, b.Name, $"account {b.Account} quest-batch {b.Name} {string.Join(',', ids)}", Snapshot(b.Account, b.Name));
        });
        app.MapPost("/api/quests/clear", (HttpContext http, NameBody b) =>
            Invalid(b.Account, b.Name, b.Confirm) is { } bad ? bad :
            Run(http, data, "quest-clear", b.Account, b.Name, $"account {b.Account} quest-clear {b.Name}", Snapshot(b.Account, b.Name)));

        app.MapPost("/api/items/delete", async (HttpContext http, DeleteItemsBody b) =>
        {
            if (Invalid(b.Account, b.Name, b.Confirm) is { } bad) return bad;
            if (b.Refs is not { Length: >= 1 and <= 20 } refs || refs.Any(r => !ItemRef.IsMatch(r))) return Bad("物品须为 1–20 个 容器:位置:UID");
            return await RunReturning(http, data, "item-delete", b.Account, b.Name, $"account {b.Account} item-delete {b.Name} {string.Join(',', refs)}", b.ReturnFirst == true);
        });
        app.MapPost("/api/items/clear", async (HttpContext http, ClearBody b) =>
        {
            if (Invalid(b.Account, b.Name, b.Confirm) is { } bad) return bad;
            if (b.Container is < 0 or > 255) return Bad("容器无效");
            return await RunReturning(http, data, "item-clear", b.Account, b.Name, $"account {b.Account} item-clear {b.Name} {b.Container}", b.ReturnFirst == true);
        });
        app.MapPost("/api/mails/delete", async (HttpContext http, DeleteMailBody b) =>
        {
            if (Invalid(b.Account, b.Name, b.Confirm) is { } bad) return bad;
            string target;
            if (b.All == true) target = "all";
            else if (b.Ids is { Length: >= 1 and <= 20 } ids) target = string.Join(',', ids);
            else return Bad("邮件须为 1–20 个 ID，或选择全部可删除邮件");
            return await RunReturning(http, data, "mail-delete", b.Account, b.Name, $"account {b.Account} mail-delete {b.Name} {target}", b.ReturnFirst == true);
        });
        app.MapPost("/api/direct/item", async (HttpContext http, DirectItemBody b) =>
        {
            if (Invalid(b.Account, b.Name, b.Confirm) is { } bad) return bad;
            if (b.ItemId <= 0 || b.Count is < 1 or > 99999 || b.Upgrade is < 0 or > 31 || b.Refine is < 0 or > 99 ||
                b.Amp is not null && !Amplify.IsMatch(b.Amp) && b.Amp != "amp:0:0" || b.Ability is < 0 or > 65535) return Bad("物品参数无效");
            if (b.ClearPeriod == true && b.Expire is not null) return Bad("期限不能同时改为不过期和指定时间");
            if (b.Expire is < 1) return Bad("到期时间无效");
            return await RunReturning(http, data, "direct-item", b.Account, b.Name,
                $"account {b.Account} grant {b.Name} {b.ItemId} {b.Count} {b.Upgrade} {b.Refine} direct{(b.Amp is null ? "" : " " + b.Amp)}{(b.Unseal == true ? " seal:open" : "")}{(b.Ability is null ? "" : " ability:" + b.Ability)}{(b.ClearPeriod == true ? " period:off" : "")}{(b.Expire is null ? "" : " expire:" + b.Expire)}", b.ReturnFirst == true);
        });
        app.MapPost("/api/items/edit", async (HttpContext http, ItemEditBody b) =>
        {
            if (Invalid(b.Account, b.Name, b.Confirm) is { } bad) return bad;
            if (b.List < 0 || b.Slot < 0 || b.Uid <= 0) return Bad("物品位置无效");
            if (b.Upgrade is < 0 or > 31 || b.Refine is < 0 or > 127 || b.Ability is < 0 or > 65535 || b.Count is < 1 or > 99999) return Bad("属性超出范围");
            if (b.Amp is not null && !Amplify.IsMatch(b.Amp) && b.Amp != "amp:0:0") return Bad("增幅参数无效");
            if (b.ClearPeriod == true && b.Expire is not null) return Bad("期限不能同时改为不过期和指定时间");
            if (b.Expire is < 1) return Bad("到期时间无效");
            var tokens = new List<string> { $"account {b.Account} item-edit {b.Name} {b.List}:{b.Slot}:{b.Uid}" };
            if (b.Upgrade is not null) tokens.Add("up:" + b.Upgrade);
            if (b.Refine is not null) tokens.Add("ref:" + b.Refine);
            if (b.Amp is not null) tokens.Add(b.Amp);
            if (b.Ability is not null) tokens.Add("ability:" + b.Ability);
            if (b.Count is not null) tokens.Add("count:" + b.Count);
            if (b.ClearPeriod == true) tokens.Add("period:off");
            if (b.Expire is not null) tokens.Add("expire:" + b.Expire);
            if (tokens.Count == 1) return Bad("没有要修改的属性");
            return await RunReturning(http, data, "item-edit", b.Account, b.Name, string.Join(' ', tokens), b.ReturnFirst == true);
        });
        app.MapPost("/api/direct/set", async (HttpContext http, DirectSetBody b) =>
        {
            if (Invalid(b.Account, b.Name, b.Confirm) is { } bad) return bad;
            if (b.SetIndex < 0 || b.Upgrade is < 0 or > 31 || b.Refine is < 0 or > 99) return Bad("套装参数无效");
            return await RunReturning(http, data, "direct-set", b.Account, b.Name, $"account {b.Account} set-grant {b.Name} {b.SetIndex} {b.Upgrade} {b.Refine} direct", b.ReturnFirst == true);
        });

        app.MapPost("/api/characters/rename", async (HttpContext http, RenameBody b) =>
        {
            if (Invalid(b.Account, b.Name, b.Confirm) is { } bad) return bad;
            if (!Token.IsMatch(b.NewName ?? "")) return Bad("新角色名无效（不能含空白或控制字符，长度 1–32）");
            return await RunReturning(http, data, "rename", b.Account, b.Name, $"account {b.Account} rename {b.Name} {b.NewName}", b.ReturnFirst == true, Snapshot(b.Account, b.Name));
        });
        // 95 服务端的角色删除是永久删除（没有软删），所以必须输入与目标一致的角色名，并在审计里保留删除前快照。
        app.MapPost("/api/characters/delete", async (HttpContext http, DeleteCharacterBody b) =>
        {
            if (Invalid(b.Account, b.Name, b.Confirm) is { } bad) return bad;
            if (!string.Equals(b.ConfirmName, b.Name, StringComparison.Ordinal)) return Bad("确认角色名与目标不一致");
            return await RunReturning(http, data, "character-delete", b.Account, b.Name, $"account {b.Account} char-delete {b.Name} {b.ConfirmName}", b.ReturnFirst == true, Snapshot(b.Account, b.Name));
        });

        // 称号簿/成就：直接完成成就（发称号、奖励与邮件），仅选角界面可写；游戏中的角色经 returnFirst 先小退。
        app.MapPost("/api/titles/complete", async (HttpContext http, TitleCompleteBody b) =>
        {
            if (Invalid(b.Account, b.Name, b.Confirm) is { } bad) return bad;
            if (!TitleSpec.IsMatch(b.Spec ?? "")) return Bad("成就范围无效");
            return await RunReturning(http, data, "title-complete", b.Account, b.Name, $"account {b.Account} title-complete {b.Name} {b.Spec}", b.ReturnFirst == true, Snapshot(b.Account, b.Name));
        });
        // 扩展装备栏：flags 只能由 1 辅助装备、2 魔法石、16 耳环按位或组成，只增不减；写角色标志须在选角界面，returnFirst 先强制小退。
        app.MapPost("/api/equip-slots", async (HttpContext http, EquipSlotsBody b) =>
        {
            if (Invalid(b.Account, b.Name, b.Confirm) is { } bad) return bad;
            if (b.Flags is <= 0 or > 255 || (b.Flags & ~19) != 0) return Bad("flags 只能由 1（辅助装备）、2（魔法石）、16（耳环）组合");
            return await RunReturning(http, data, "equip-slots", b.Account, b.Name, $"account {b.Account} equip-slots {b.Name} {b.Flags}", b.ReturnFirst == true,
                $"account {b.Account} equip-slots {b.Name}");
        });
        // 账号金库金币：账号共享；命令层在线安全（owner 内提交并同步在线快照），所以不需要小退。
        app.MapPost("/api/vault-gold", (HttpContext http, VaultGoldBody b) =>
        {
            if (Invalid(b.Account, b.Name, b.Confirm) is { } bad) return bad;
            if (b.Op is not ("set" or "add" or "sub") || b.Amount is < 0 or > int.MaxValue) return Bad("金币参数无效");
            return Run(http, data, "vault-gold", b.Account, b.Name, $"account {b.Account} currency {b.Name} vault-gold {b.Op} {b.Amount}", $"account {b.Account} wallet {b.Name}");
        });
        app.MapGet("/api/quick-items", () => Results.Text("{\"ok\":true,\"data\":" + data.ReadQuickItems() + "}", "application/json"));
        app.MapPost("/api/quick-items", (HttpContext http, QuickItemsBody b) =>
        {
            if (b.Confirm != true) return Bad("需要二次确认");
            if (b.Items is not { Length: <= 100 } items || items.Any(i => i is null || i.Id <= 0 || i.Count is < 1 or > 99999 || i.Upgrade is < 0 or > 31 || i.Refine is < 0 or > 99 ||
                i.Label is { Length: > 30 } || i.Label is not null && i.Label.Any(char.IsControl))) return Bad("快捷物品最多 100 个，参数须合法");
            string json = JsonSerializer.Serialize(items);
            data.WriteQuickItems(json);
            data.Audit.Append(new(DateTimeOffset.Now, Operator(http), "quick-items", "-", "保存 " + items.Length + " 个快捷物品", true, "", "", "OK"));
            return Results.Json(new { ok = true, data = items.Length });
        });
        app.MapGet("/api/events", () => Reply(data.Command("event status 64")));
        app.MapPost("/api/events", (HttpContext http, EventBody b) =>
        {
            if (b.Confirm != true) return Bad("需要二次确认");
            if (b.Id != 64) return Bad("未注册的活动");
            if (b.Op is not ("on" or "off" or "auto" or "schedule")) return Bad("操作无效");
            string command = $"event {b.Op} {b.Id}";
            if (b.Op == "schedule")
            {
                if (!EventTime.IsMatch(b.Start ?? "") || !EventTime.IsMatch(b.End ?? "")) return Bad("时间须为 ISO8601，如 2026-10-20T00:00:00+08:00 或以 Z 结尾");
                command = $"event schedule {b.Id} {b.Start} {b.End}";
            }
            return Run(http, data, "event-" + b.Op, "-", "活动" + b.Id, command, "event status 64");
        });

        // 账号钱包由同账号角色共享：取该账号第一个角色作为入口，并要求货币确实是账号级。
        app.MapPost("/api/account-currency", (HttpContext http, AccountCurrencyBody b) =>
        {
            if (!Token.IsMatch(b.Account ?? "") || !Token.IsMatch(b.Key ?? "")) return Bad("账号或货币无效");
            if (b.Confirm != true) return Bad("需要二次确认");
            if (b.Op is not ("set" or "add" or "sub") || b.Amount is < 0 or > int.MaxValue) return Bad("货币参数无效");
            if (data.Characters(b.Account!)?.FirstOrDefault() is not { } entry) return Bad("账号不存在或没有角色");
            string wallet = data.Command($"account {b.Account} wallet {entry.Name}");
            if (!wallet.StartsWith("OK ", StringComparison.Ordinal)) return Reply(wallet);
            using var document = JsonDocument.Parse(wallet[3..]);
            bool accountScoped = document.RootElement.TryGetProperty("currencies", out var rows) &&
                rows.EnumerateArray().Any(row => row.GetProperty("key").GetString() == b.Key && row.GetProperty("account").GetBoolean());
            if (!accountScoped) return Bad("该货币不是账号级货币");
            return Run(http, data, "account-currency", b.Account!, entry.Name, $"account {b.Account} currency {entry.Name} {b.Key} {b.Op} {b.Amount}", $"account {b.Account} wallet {entry.Name}");
        });
    }

    static readonly Regex TitleSpec = new("^(all|book|book:(general|specific|pvp|despair|event)|[0-9]{1,9}(,[0-9]{1,9}){0,499})$", RegexOptions.Compiled);
    static string Snapshot(string account, string name) => $"account {account} show {name}";

    static IResult Points(HttpContext http, IGmWebData data, string kind, PointsBody b)
    {
        if (Invalid(b.Account, b.Name, b.Confirm) is { } bad) return bad;
        if (b.Value is < 0 or > 50000) return Bad("额外点数须在 0–50000");
        return Run(http, data, "extra-" + kind, b.Account, b.Name, $"account {b.Account} {kind} {b.Name} {b.Value}", $"account {b.Account} points {b.Name}");
    }

    static IResult Quest(HttpContext http, IGmWebData data, string action, string command, QuestBody b)
    {
        if (Invalid(b.Account, b.Name, b.Confirm) is { } bad) return bad;
        if (b.Quest is < 1 or > 29999) return Bad("任务编号须在 1–29999");
        return Run(http, data, action, b.Account, b.Name, $"account {b.Account} {command} {b.Name} {b.Quest}", Snapshot(b.Account, b.Name));
    }

    // 命令层拒绝在游戏中的角色时：returnFirst=true 才先强制小退再重试一次；否则原样返回提示。两步都写入审计。
    static async Task<IResult> RunReturning(HttpContext http, IGmWebData data, string action, string account, string name, string command, bool returnFirst, string? snapshot = null)
    {
        string before = snapshot is null ? "" : data.Command(snapshot);
        string result = data.Command(command);
        string note = "";
        if (returnFirst && result.StartsWith("ERR ", StringComparison.Ordinal) && result.Contains(InGame, StringComparison.Ordinal))
        {
            bool returned = await data.ForceReturnAsync(account, name);
            data.Audit.Append(new(DateTimeOffset.Now, Operator(http), "force-return", account + "/" + name, "force-return（" + action + " 前置）", returned, "", "",
                returned ? "OK 已回到选角" : "ERR 未能回到选角"));
            if (returned)
            {
                note = "已先强制小退；";
                result = data.Command(command);
            }
        }
        bool ok = result.StartsWith("OK ", StringComparison.Ordinal);
        data.Audit.Append(new(DateTimeOffset.Now, Operator(http), action, account + "/" + name, command, ok, before, "", note + result));
        return Reply(result, note.Length != 0);
    }
}