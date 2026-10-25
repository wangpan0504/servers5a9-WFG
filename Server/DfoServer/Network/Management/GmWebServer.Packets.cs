using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DfoServer.Network.Management;

internal static partial class GmWebServer
{
    static void MapPacketQueries(WebApplication app, PacketCapture capture, ServerOptions options,
        DateTimeOffset startedAt, SemaphoreSlim requests, CancellationToken stop)
    {
        app.MapGet("/api/packets", (HttpContext http) => Query(http, records: true));
        app.MapGet("/api/packets/sessions", (HttpContext http) => Query(http));
        app.MapGet("/api/packets/status", (HttpContext http) => Query(http));

        async Task Query(HttpContext http, bool records = false)
        {
            string authority = $"127.0.0.1:{http.Connection.LocalPort}";
            string localhost = $"localhost:{http.Connection.LocalPort}";
            string? host = http.Request.Host.Value;
            string origin = http.Request.Headers.Origin.ToString();
            if (host != authority && host != localhost || origin.Length != 0 &&
                origin != "http://" + authority && origin != "http://" + localhost)
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (http.Request.ContentLength is > 0 || http.Request.Headers.ContainsKey("Transfer-Encoding") ||
                http.Request.Headers.Sum(header => header.Key.Length + header.Value.ToString().Length + 4) > 8192)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            if (!await requests.WaitAsync(0, http.RequestAborted))
            {
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, stop);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                int? connection = PacketQueries.ConnectionFilter(http.Request.QueryString.Value ?? "");
                if (!records && connection is not null)
                    throw new InvalidDataException("Connection filter requires packet records");
                object result = records ? capture.Recent(connection) : http.Request.Path == "/api/packets/sessions"
                    ? capture.Sessions() : PacketQueries.Status(capture, options, startedAt);
                await http.Response.WriteAsJsonAsync(result, cancellationToken: deadline.Token);
            }
            catch (InvalidDataException)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                http.Abort();
            }
            catch (IOException)
            {
                http.Abort();
            }
            finally
            {
                requests.Release();
            }
        }
    }
}
