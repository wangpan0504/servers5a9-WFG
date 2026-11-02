using System.Text.Json;
using DfoServer.Game.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DfoServer.Network.Management;

internal static partial class GmWebServer
{
    sealed record EventControlState(int EventId, bool Active, string Mode, long Revision, long? StartsAt, long? EndsAt);

    static void MapEvents(WebApplication app, IGmWebData data)
    {
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
            var related = new[]
            {
                new { id = 2037, name = "赛丽亚幸运值", purpose = "开盒累计幸运阶段并按已有规则发放奖励。", conditions = "账号进度与真实开盒奖励已接通。", controlReason = "属于常驻业务，没有全服活动开关。" },
                new { id = 2378, name = "公会红包", purpose = "按公会红包现有规则购买、发放和领取。", conditions = "费用、资格与领取记录沿原事务处理。", controlReason = "属于常驻业务，没有通用活动开关。" },
                new { id = 2395, name = "红尾乔纳森显示", purpose = "显示乔纳森，并沿时空石商店原规则交易。", conditions = "当前城镇目录固定提供此NPC显示项。", controlReason = "当前没有单独的运营开关。" },
                new { id = 10999, name = "众神降世礼包 多重购买有惊喜", purpose = "按真实礼包购买账本展示累计购买奖励。", conditions = "可用性取决于礼包独立配置、商品和奖励资源校验。", controlReason = "由礼包配置控制，不能使用通用活动开关。" }
            }.Select(entry => new
            {
                entry.id, entry.name, entry.purpose, entry.conditions, supportsControl = false, supportsSchedule = false,
                entry.controlReason, defaultState = "沿现有玩法规则", endpoint = (string?)null,
                state = (EventControlState?)null, stateError = (string?)null
            });
            return Results.Json(new { ok = true, data = new
            {
                schemaVersion = 1, serverTimeUtc = DateTimeOffset.UtcNow, events = controlled.Concat(related),
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
