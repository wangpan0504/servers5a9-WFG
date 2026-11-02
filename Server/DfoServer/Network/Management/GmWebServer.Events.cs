using System.Text.Json;
using DfoServer.Game.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DfoServer.Network.Management;

internal static partial class GmWebServer
{
    sealed record EventControlState(int EventId, bool Active, string Mode, long Revision, long? StartsAt, long? EndsAt);
    sealed record PromotionControlBody(GameWorld.Items.PromotionScope Scope, long ExpectedRevision, bool? Enabled, bool? Confirm);

    internal sealed record EventDefaultBody(bool? Confirm, int? ExpectedPid, int Id);

    static void MapEvents(WebApplication app, IGmWebData data)
    {
        app.MapPost("/api/events/default", (HttpContext http, EventDefaultBody b) =>
        {
            if (b.Confirm != true) return Bad("需要确认后才初始化默认活动");
            if (b.ExpectedPid is { } pid && pid != data.Status().Pid) return Bad("目标服务端进程已改变，请重新连接");
            if (b.Id is not (312 or 2520)) return Bad("该活动不支持首次默认初始化");
            return Run(http, data, "event-initialize-default", "-", ServerEventDefinition.Find(b.Id)!.Name,
                $"event initialize-default {b.Id}", $"event status {b.Id}");
        });
        app.MapGet("/api/events/promotions", () => Results.Json(new { ok = true, data = data.PromotionActivities() }));
        app.MapPost("/api/events/promotions", (HttpContext http, PromotionControlBody body) =>
        {
            if (body.Confirm != true || body.Enabled is null || body.ExpectedRevision <= 0 ||
                string.IsNullOrWhiteSpace(body.Scope.Campaign) || string.IsNullOrWhiteSpace(body.Scope.Period) ||
                string.IsNullOrWhiteSpace(body.Scope.RulesVersion) || string.IsNullOrWhiteSpace(body.Scope.Realm) || string.IsNullOrWhiteSpace(body.Scope.Group))
                return Bad("需要完整礼包身份、当前版本和明确开关确认");
            string target = JsonSerializer.Serialize(body.Scope);
            try
            {
                var result = data.SetPromotionEnabled(body.Scope, body.ExpectedRevision, body.Enabled.Value);
                data.Audit.Append(new(DateTimeOffset.Now, Operator(http), "promotion-control", target,
                    body.Enabled.Value ? "on" : "off", true, body.ExpectedRevision.ToString(), JsonSerializer.Serialize(result), "OK"));
                return Results.Json(new { ok = true, data = result });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or ArgumentException)
            {
                data.Audit.Append(new(DateTimeOffset.Now, Operator(http), "promotion-control", target,
                    body.Enabled.Value ? "on" : "off", false, body.ExpectedRevision.ToString(), "", error.Message));
                return Bad(error.Message);
            }

        });
        app.MapGet("/api/events/catalog", () =>
        {
            var controlled = ServerEventDefinition.Registered.Select(entry =>
            {
                var state = ReadEventState(data, entry.Id);
                return new
                {
                    id = (int)entry.Id, name = entry.Name, purpose = entry.Purpose, conditions = entry.Conditions,
                    supportsControl = true, supportsSchedule = entry.SupportsSchedule, controlReason = "", defaultState = entry.DefaultState,
                    endpoint = (string?)"/api/events", state = state.State, stateError = state.Error
                };
            }).ToArray();
            return Results.Json(new { ok = true, data = new
            {
                schemaVersion = 1, serverTimeUtc = DateTimeOffset.UtcNow, events = controlled,
                relatedBusiness = data.EventBusinessStatus()
            } });
        });
        app.MapGet("/api/events/resources", (int? offset, int? limit, string? q, string? kind) =>
        {
            if (offset < 0 || limit is < 1 or > 100 || q?.Length > 100 ||
                kind is not (null or "all" or "definition" or "component" or "directory" or "list")) return Bad("活动资源查询参数无效");
            var resources = data.EventResources(offset ?? 0, limit ?? 50, q, kind ?? "definition");
            return resources is null ? Bad("当前服务端未装配活动资源目录") : Results.Json(new { ok = true, data = resources });
        });
        app.MapGet("/api/events", (int? id) =>
        {
            int eventId = id ?? ServerEventService.DoubleGoldCardId;
            return ServerEventDefinition.Find(eventId) is not null
                ? Reply(data.Command($"event status {eventId}")) : Bad("该条目没有已接通的活动开关");
        });
        app.MapPost("/api/events", (HttpContext http, EventBody b) =>
        {
            if (b.Confirm != true) return Bad("需要二次确认");
            var definition = ServerEventDefinition.Find(b.Id);
            if (definition is null) return Bad("该条目没有已接通的活动开关");
            if (b.Op is not ("on" or "off" or "auto" or "schedule")) return Bad("操作无效");
            string command = $"event {b.Op} {b.Id}";
            if (b.Op == "schedule")
            {
                if (!definition.SupportsSchedule) return Bad("该活动不支持设置起止时间");
                if (!EventTime.IsMatch(b.Start ?? "") || !EventTime.IsMatch(b.End ?? ""))
                    return Bad("请填写带时区的开始和结束时间，例如2026-11-02T09:00:00+08:00");
                command = $"event schedule {b.Id} {b.Start} {b.End}";
            }
            return Run(http, data, "event-" + b.Op, "-", definition.Name, command, $"event status {b.Id}");
        });
    }

    static (EventControlState? State, string? Error) ReadEventState(IGmWebData data, int id)
    {
        string reply = data.Command($"event status {id}");
        if (!reply.StartsWith("OK ", StringComparison.Ordinal))
            return (null, reply.StartsWith("ERR ", StringComparison.Ordinal) ? reply[4..] : reply);
        try
        {
            using var document = JsonDocument.Parse(reply[3..]);
            var root = document.RootElement;
            long? Point(string key) => root.GetProperty(key).ValueKind == JsonValueKind.Null ? null : root.GetProperty(key).GetInt64();
            return (new(root.GetProperty("eventId").GetInt32(), root.GetProperty("active").GetBoolean(),
                root.GetProperty("mode").GetString() ?? "", root.GetProperty("Revision").GetInt64(), Point("StartsAt"), Point("EndsAt")), null);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return (null, "活动状态回执不完整，未推断开启或关闭");
        }
    }
}
