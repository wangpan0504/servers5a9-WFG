using System.Globalization;
using System.Text.Json;
using DfoServer.Game.Currency;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DfoServer.Network.Management;

internal static partial class GmWebServer
{
    internal const string RechargeRoot = "/comm-htdocs/pay/client/dnf/";

    static void MapRecharge(WebApplication app, IGmWebData data)
    {
        string page = Assets.Text("recharge.html");
        var requests = new SemaphoreSlim(8, 8);
        app.Lifetime.ApplicationStopped.Register(requests.Dispose);
        foreach (string path in new[] { RechargeRoot, RechargeRoot + "index.html", RechargeRoot + "diamond/", RechargeRoot + "diamond/index.html" })
            app.MapGet(path, () => Results.Text(page, "text/html; charset=utf-8"));
        app.MapPost(RechargeRoot + "api/status", (Func<HttpContext, Task<IResult>>)(context => RechargeRequest(context, data, requests, false)));
        app.MapPost(RechargeRoot + "api/recharge", (Func<HttpContext, Task<IResult>>)(context => RechargeRequest(context, data, requests, true)));
    }

    static async Task<IResult> RechargeRequest(HttpContext context, IGmWebData data, SemaphoreSlim requests, bool writing)
    {
        if (!requests.Wait(0)) return Results.Json(new { ok = false, error = "充值请求繁忙，请稍后查询" }, statusCode: 429);
        try
        {
            if (!context.Request.HasJsonContentType())
                return Results.Json(new { ok = false, error = "请求必须是JSON" }, statusCode: 415);
            if (context.Request.ContentLength is not (> 0 and <= 2048))
                return Results.Json(new { ok = false, error = "请求正文长度无效" }, statusCode: 413);
            using var body = await JsonDocument.ParseAsync(context.Request.Body,
                new JsonDocumentOptions { MaxDepth = 8 }, context.RequestAborted);
            var value = body.RootElement;
            if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("请求必须是JSON对象");
            string[] allowed = writing ? ["ticket", "ticketKey", "kind", "amount"] : ["ticket", "ticketKey"];
            var fields = value.EnumerateObject().Select(property => property.Name).ToArray();
            if (fields.Length != allowed.Length || fields.Distinct(StringComparer.Ordinal).Count() != fields.Length || fields.Any(name => !allowed.Contains(name)))
                throw new InvalidDataException("只接受当前游戏票据，不能另行指定账号");
            if (!value.TryGetProperty("ticket", out var tokenValue) || tokenValue.ValueKind != JsonValueKind.String ||
                tokenValue.GetString() is not { Length: <= 63 } token ||
                !value.TryGetProperty("ticketKey", out var keyValue) || keyValue.ValueKind != JsonValueKind.Number || !keyValue.TryGetUInt32(out uint key) || key is 0 or > 65535)
                throw new InvalidDataException("充值票据无效，请从游戏内重新打开充值页");
            string account = RechargeTicket.Account(token);
            string command = "recharge-ticket-status " + token + " " + key.ToString(CultureInfo.InvariantCulture);
            if (writing)
            {
                if (!value.TryGetProperty("kind", out var kindValue) || kindValue.ValueKind != JsonValueKind.String ||
                    kindValue.GetString() is not ("cera" or "black-diamond") ||
                    !value.TryGetProperty("amount", out var amountValue) || amountValue.ValueKind != JsonValueKind.Number || !amountValue.TryGetInt32(out int amount) || amount < 1)
                    throw new InvalidDataException("充值类型或数量无效");
                command = "recharge-ticket " + token + " " + key.ToString(CultureInfo.InvariantCulture) + " " +
                    kindValue.GetString() + " " + amount.ToString(CultureInfo.InvariantCulture);
            }
            // 仅转发已验证的两类固定票据命令，复用原owner、资产事务及错误合同。
            string reply = data.Command(command);
            if (reply.StartsWith("ERR ", StringComparison.Ordinal))
                return Results.Json(new { ok = false, error = reply[4..] }, statusCode: 409);
            if (!reply.StartsWith("OK ", StringComparison.Ordinal)) throw new IOException("充值回执无效");
            using var result = JsonDocument.Parse(reply[3..]);
            var receipt = result.RootElement;
            if (receipt.GetProperty("account").GetString() != account) throw new IOException("充值账号回执不匹配");
            return Results.Json(new { ok = true, result = receipt.Clone() });
        }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        {
            return Results.Json(new { ok = false, error = "充值请求无效，请从游戏内重新打开充值页" }, statusCode: 400);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or KeyNotFoundException)
        {
            return Results.Json(new { ok = false, uncertain = writing, error = "结果未确认，请查询余额核对，不要重复提交" }, statusCode: 502);
        }
        finally { requests.Release(); }
    }
}
