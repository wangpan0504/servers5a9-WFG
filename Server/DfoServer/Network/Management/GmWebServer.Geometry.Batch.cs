using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DfoServer.Network.Management;

internal static partial class GmWebServer
{
    internal const string GeometryVersionRoute = "/client/displaycompat-world/version";
    internal const string GeometryBatchRoute = "/client/displaycompat-world/batch";
    internal const int GeometryBatchLimit = 64;
    internal const int GeometryBatchRequestLimit = 4 + 32 + 2 + GeometryBatchLimit * (2 + MapGeometryHttpSnapshot.MaxPathBytes);

    internal static void MapClientGeometryBatch(WebApplication app, Func<MapGeometryHttpSnapshot?> snapshot)
    {
        // 注册时冻结真实进程身份；读取失败直接终止装配，不以默认值冒充响应归属。
        using var process = Process.GetCurrentProcess();
        string processId = process.Id.ToString(CultureInfo.InvariantCulture);
        string processStartUtc = process.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        app.MapGet(GeometryVersionRoute, (HttpContext context) =>
        {
            context.Response.Headers["X-95JP-Process-Id"] = processId;
            context.Response.Headers["X-95JP-Process-Start-Utc"] = processStartUtc;
            return snapshot() is { } current
                ? Results.Bytes(current.Version, "application/octet-stream") : Results.StatusCode(503);
        });
        // POST是有界只读查询；所有条目取自同一快照，任一失败不返回部分许可。
        app.MapPost(GeometryBatchRoute, async (HttpContext context) =>
        {
            if (!string.Equals(context.Request.ContentType?.Split(';')[0].Trim(), "application/octet-stream", StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(415);
            if (context.Request.ContentLength > GeometryBatchRequestLimit) return Results.StatusCode(413);
            using var body = new MemoryStream();
            byte[] buffer = new byte[4096];
            while (true)
            {
                int read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted);
                if (read == 0) break;
                if (body.Length + read > GeometryBatchRequestLimit) return Results.StatusCode(413);
                body.Write(buffer, 0, read);
            }
            return GeometryBatchReply(snapshot(), body.GetBuffer().AsSpan(0, checked((int)body.Length)));
        });
    }

    internal static IResult GeometryBatchReply(MapGeometryHttpSnapshot? snapshot, ReadOnlySpan<byte> request)
    {
        if (snapshot is null) return Results.StatusCode(503);
        if (request.Length < 38 || !request[..4].SequenceEqual("DGB1"u8)) return Results.BadRequest();
        int count = BinaryPrimitives.ReadUInt16LittleEndian(request[36..]);
        if (count is < 1 or > GeometryBatchLimit) return Results.BadRequest();
        var paths = new List<string>(count);
        var unique = new HashSet<string>(StringComparer.Ordinal);
        int offset = 38;
        try
        {
            for (int i = 0; i < count; ++i)
            {
                if (request.Length - offset < 2) return Results.BadRequest();
                int length = BinaryPrimitives.ReadUInt16LittleEndian(request[offset..]); offset += 2;
                if (length is < 1 or > MapGeometryHttpSnapshot.MaxPathBytes || request.Length - offset < length) return Results.BadRequest();
                string path = MapGeometryHttpSnapshot.Utf8.GetString(request.Slice(offset, length)); offset += length;
                if (!MapGeometryHttpSnapshot.ValidPath(path) || !unique.Add(path)) return Results.BadRequest();
                paths.Add(path);
            }
        }
        catch (DecoderFallbackException) { return Results.BadRequest(); }
        if (offset != request.Length) return Results.BadRequest();
        if (!snapshot.HasVersion(request.Slice(4, 32))) return Results.Conflict();
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, MapGeometryHttpSnapshot.Utf8, true);
        writer.Write("DGS1"u8); writer.Write(snapshot.Version); writer.Write(checked((ushort)paths.Count));
        foreach (string path in paths)
        {
            byte[]? signature = snapshot.Signature(path);
            if (signature is null) return Results.NotFound();
            byte[] encoded = MapGeometryHttpSnapshot.Utf8.GetBytes(path);
            writer.Write(checked((ushort)encoded.Length)); writer.Write(encoded); writer.Write(signature);
        }
        writer.Flush();
        return Results.Bytes(bytes.ToArray(), "application/octet-stream");
    }
}
