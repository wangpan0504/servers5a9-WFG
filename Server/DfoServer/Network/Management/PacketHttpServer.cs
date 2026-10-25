using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Management;

internal sealed record OnlineCharacterView(string Account, ushort CharacterId, string Name, bool Ready, string Phase, string UserState);

// 沿用 GM 网页的本机 TCP HTTP 边界，不依赖 Windows HTTP.sys 权限或访问玩家库。
internal static class PacketHttpServer
{
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static Task Run(int port, PacketCapture capture, ServerOptions options, Func<OnlineCharacterView[]> onlineCharacters,
        DateTimeOffset startedAt, ServerLog log, CancellationToken stop)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start(8);
            return Loop(listener, capture, options, onlineCharacters, startedAt, log, stop);
        }
        catch
        {
            listener.Stop();
            throw;
        }
    }

    static async Task Loop(TcpListener listener, PacketCapture capture, ServerOptions options,
        Func<OnlineCharacterView[]> onlineCharacters, DateTimeOffset startedAt, ServerLog log, CancellationToken stop)
    {
        var capacity = new SemaphoreSlim(8, 8);
        var clients = new HashSet<Task>();
        string authority = $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await capacity.WaitAsync(stop);
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop); }
                catch { capacity.Release(); throw; }
                var task = Respond(client, authority, capture, options, onlineCharacters, startedAt, log, stop);
                lock (clients) clients.Add(task);
                _ = task.ContinueWith(completed =>
                {
                    lock (clients) clients.Remove(completed);
                    capacity.Release();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (SocketException error) when (stop.IsCancellationRequested && error.SocketErrorCode == SocketError.OperationAborted) { }
        finally
        {
            listener.Stop();
            Task[] pending;
            lock (clients) pending = clients.ToArray();
            await Task.WhenAll(pending);
            // 完成回调仍可能释放 capacity；与 GM 网页一样由本次监听生命周期回收。
        }
    }

    sealed record Request(string Method, string Target, Dictionary<string, string> Headers);

    static async Task<Request> ReadRequest(NetworkStream stream, CancellationToken stop)
    {
        var buffer = new byte[8192];
        int size = 0;
        int end = -1;
        while (size < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(size), stop);
            if (read == 0) throw new IOException("Incomplete HTTP header");
            size += read;
            end = buffer.AsSpan(0, size).IndexOf("\r\n\r\n"u8);
            if (end >= 0) break;
        }
        if (end < 0) throw new InvalidDataException("HTTP header too large");
        var rows = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n", StringSplitOptions.None);
        var first = rows[0].Split(' ');
        if (first.Length != 3 || first[2] is not ("HTTP/1.0" or "HTTP/1.1") ||
            first[0].Length == 0 || !first[0].All(char.IsAsciiLetter) ||
            !first[1].StartsWith('/') || first[1].StartsWith("//", StringComparison.Ordinal) || first[1].Contains('#'))
            throw new InvalidDataException("Invalid HTTP request line");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string row in rows.Skip(1))
        {
            int at = row.IndexOf(':');
            if (at <= 0 || !headers.TryAdd(row[..at], row[(at + 1)..].Trim()))
                throw new InvalidDataException("Invalid or duplicate HTTP header");
        }
        if (headers.ContainsKey("Transfer-Encoding") || headers.TryGetValue("Content-Length", out string? length) &&
            (!int.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out int count) ||
                first[0] == "GET" && count != 0))
            throw new InvalidDataException("Request bodies are not supported");
        return new(first[0], first[1], headers);
    }

    static async Task Respond(TcpClient client, string authority, PacketCapture capture, ServerOptions options,
        Func<OnlineCharacterView[]> onlineCharacters, DateTimeOffset startedAt, ServerLog log, CancellationToken stop)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var stream = client.GetStream();
            try
            {
                var request = await ReadRequest(stream, deadline.Token);
                string localhost = authority.Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
                string? host = request.Headers.GetValueOrDefault("Host");
                string? origin = request.Headers.GetValueOrDefault("Origin");
                if (client.Client.RemoteEndPoint is not IPEndPoint peer || !IPAddress.IsLoopback(peer.Address) ||
                    host != authority && host != localhost || origin is not null && origin != "http://" + authority && origin != "http://" + localhost)
                    await Reply(stream, 403, new { Error = "Local requests only" }, deadline.Token);
                else if (request.Method != "GET")
                    await Reply(stream, 405, new { Error = "GET required" }, deadline.Token);
                else
                {
                    if (!Uri.TryCreate("http://" + authority + request.Target, UriKind.Absolute, out var url))
                        throw new InvalidDataException("Invalid request target");
                    int? connectionId = PacketQueries.ConnectionFilter(url.Query);
                    if (connectionId is not null && url.AbsolutePath != "/api/packets")
                        throw new InvalidDataException("Connection filter requires /api/packets");
                    object? result = url.AbsolutePath switch
                    {
                        "/health" => new { Status = "ok", Service = "ServerS5A9" },
                        "/api/status" => PacketQueries.Status(capture, options, startedAt),
                        "/api/sessions" => capture.Sessions(),
                        "/api/packets" => capture.Recent(connectionId),
                        "/api/characters/online" => onlineCharacters(),
                        _ => null
                    };
                    await Reply(stream, result is null ? 404 : 200, result ?? new { Error = "Unknown path" }, deadline.Token);
                }
            }
            catch (InvalidDataException)
            {
                await Error(400, "Invalid request");
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { }
            catch (Exception error)
            {
                log.Write("packet_http_failed", new { Reason = error.GetType().Name });
                await Error(500, "Query failed");
            }

            async Task Error(int status, string message)
            {
                try { await Reply(stream, status, new { Error = message }, deadline.Token); }
                catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { }
            }
        }
    }

    static async Task Reply(NetworkStream stream, int status, object result, CancellationToken stop)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(result, result.GetType(), JsonOptions);
        string reason = status switch
        {
            200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Internal Server Error"
        };
        string allow = status == 405 ? "Allow: GET\r\n" : "";
        byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n{allow}\r\n");
        await stream.WriteAsync(header, stop);
        await stream.WriteAsync(body, stop);
    }
}
