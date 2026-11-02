using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DfoServer.Network.Management;

internal static partial class GmWebServer
{
    internal static void MapClientGeometry(WebApplication app, Func<string, byte[]?>? geometry)
    {
        // 公开只读能力；仅查启动时已装载的有界快照，不借用GM凭据或在线角色锁。
        app.MapGet("/client/displaycompat-world", (string? map) =>
        {
            if (map is null || map.Length > 480 || !map.StartsWith("map/", StringComparison.Ordinal) ||
                map.Contains("..", StringComparison.Ordinal) || map.IndexOfAny(['\\', ':', '\0']) >= 0)
                return Results.BadRequest();
            byte[]? signature = geometry?.Invoke(map);
            return signature is { Length: 32 } ? Results.Bytes(signature, "application/octet-stream") : Results.NotFound();
        });
    }
}
