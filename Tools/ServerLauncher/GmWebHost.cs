using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ServerLauncher;

// 同一启动器拥有网页监听；只代理本次已就绪服务的本机GM端口，不直接访问玩家库。
internal sealed class GmWebHost : IAsyncDisposable
{
    readonly TcpListener listener;
    readonly CancellationTokenSource stopping = new();
    readonly SemaphoreSlim capacity = new(8);
    readonly HashSet<Task> clients = [];
    readonly Action<string> log;
    readonly Func<bool> serviceAvailable;
    readonly Func<TcpClient, bool> ownsConnection;
    readonly int gmPort;
    readonly Task accepting;
    Task? disposing;
    static readonly byte[] Page = LoadPage();
    public string Url { get; }

    public GmWebHost(int gmPort, int webPort, Action<string> log, Func<bool>? serviceAvailable = null,
        Func<TcpClient, bool>? ownsConnection = null)
    {
        if (gmPort is < 1 or > 65535 || webPort is < 1 or > 65535 || gmPort == webPort)
            throw new InvalidDataException("GM 与网页端口必须有效且互不相同。");
        this.gmPort = gmPort;
        this.log = log;
        this.serviceAvailable = serviceAvailable ?? (() => true);
        this.ownsConnection = ownsConnection ?? (_ => true);
        Url = $"http://127.0.0.1:{webPort}";
        listener = new(IPAddress.Loopback, webPort);
        try { listener.Start(8); }
        catch (SocketException error) { throw new IOException($"GM 网页端口 {webPort} 已被占用或不可监听。", error); }
        accepting = AcceptAsync();
    }

    static byte[] LoadPage()
    {
        using var stream = typeof(GmWebHost).Assembly.GetManifestResourceStream("ServerLauncher.GmWeb.html")
            ?? throw new InvalidDataException("程序缺少内置 GM 网页。");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                await capacity.WaitAsync(stopping.Token);
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stopping.Token); }
                catch { capacity.Release(); throw; }
                var task = HandleAsync(client);
                lock (clients) clients.Add(task);
                _ = task.ContinueWith(completed =>
                {
                    lock (clients) clients.Remove(completed);
                    capacity.Release();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (SocketException) when (stopping.IsCancellationRequested) { }
    }

    async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, deadline.Token);
                bool local = request.Headers.GetValueOrDefault("Host") is { } host &&
                    (host == Url[7..] || host == Url[7..].Replace("127.0.0.1", "localhost", StringComparison.Ordinal));
                string? origin = request.Headers.GetValueOrDefault("Origin");
                if (!local || origin is not null && origin != Url && origin != Url.Replace("127.0.0.1", "localhost", StringComparison.Ordinal))
                {
                    await ReplyAsync(stream, 403, new { ok = false, error = "网页只接受本机访问" }, deadline.Token);
                    return;
                }
                string path = request.Path.Split('?')[0];
                if (request.Method == "GET" && path == "/")
                    await SendAsync(stream, 200, Page, "text/html; charset=utf-8", deadline.Token);
                else if (request.Method == "GET" && path == "/api/meta")
                    await ReplyAsync(stream, 200, new { ok = true, gmPort, account = "10000", webPort = listener.LocalEndpoint is IPEndPoint endpoint ? endpoint.Port : 0 }, deadline.Token);
                else if (request.Method == "POST" && path == "/api/command")
                    await CommandAsync(stream, request.Body, deadline.Token);
                else
                    await ReplyAsync(stream, 404, new { ok = false, error = "未知页面" }, deadline.Token);
            }
            catch (InvalidDataException)
            {
                try { await ReplyAsync(client.GetStream(), 400, new { ok = false, error = "请求无效或过长" }, deadline.Token); }
                catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { }
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { }
        }
    }

    async Task CommandAsync(NetworkStream stream, byte[] body, CancellationToken token)
    {
        string? line;
        try
        {
            using var document = JsonDocument.Parse(body);
            line = document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("line", out var value) &&
                value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException) { line = null; }
        if (string.IsNullOrWhiteSpace(line) || line.Length > 512 || line.Contains('\r') || line.Contains('\n'))
        {
            await ReplyAsync(stream, 400, new { ok = false, error = "命令无效" }, token);
            return;
        }
        try
        {
            if (!serviceAvailable()) throw new IOException("本次游戏服务已退出。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Loopback, gmPort, timeout.Token);
            if (!serviceAvailable() || !ownsConnection(client)) throw new IOException("GM连接不属于本次游戏服务。");
            var gm = client.GetStream();
            await gm.WriteAsync(Encoding.UTF8.GetBytes(line.Trim() + "\n"), timeout.Token);
            using var response = new MemoryStream();
            var buffer = new byte[4096];
            bool complete = false;
            while (response.Length <= 1024 * 1024)
            {
                int size = await gm.ReadAsync(buffer, timeout.Token);
                if (size == 0) break;
                int end = Array.IndexOf(buffer, (byte)'\n', 0, size);
                response.Write(buffer, 0, end < 0 ? size : end);
                if (end >= 0) { complete = true; break; }
            }
            if (!complete || response.Length is 0 or > 1024 * 1024)
                throw new IOException("GM 回复不完整、为空或过长。");
            await ReplyAsync(stream, 200, new { ok = true, text = Encoding.UTF8.GetString(response.ToArray()).TrimEnd('\r') }, token);
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException && !token.IsCancellationRequested)
        {
            log("GM 请求未完成，请核对本次服务和日志；写入结果不明时先读取状态，不自动重试。");
            await ReplyAsync(stream, 502, new { ok = false, error = $"GM 请求未完成（127.0.0.1:{gmPort}）。请读取状态核对结果；不要重复提交未知结果的写入。" }, token);
        }
    }

    sealed record Request(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);

    static async Task<Request> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[8192];
        int size = 0;
        while (size < header.Length)
        {
            await stream.ReadExactlyAsync(header.AsMemory(size, 1), token);
            size++;
            if (size >= 4 && header.AsSpan(size - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
        }
        if (size == header.Length) throw new InvalidDataException();
        var rows = Encoding.ASCII.GetString(header, 0, size).Split("\r\n", StringSplitOptions.None);
        var first = rows[0].Split(' ');
        if (first.Length != 3 || first[0] is not ("GET" or "POST") || first[2] is not ("HTTP/1.0" or "HTTP/1.1"))
            throw new InvalidDataException();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Skip(1).Where(row => row.Length != 0))
        {
            int at = row.IndexOf(':');
            if (at <= 0 || !headers.TryAdd(row[..at], row[(at + 1)..].Trim())) throw new InvalidDataException();
        }
        if (headers.ContainsKey("Transfer-Encoding")) throw new InvalidDataException();
        int length = 0;
        if (headers.TryGetValue("Content-Length", out var value) && (!int.TryParse(value, out length) || length is < 0 or > 4096))
            throw new InvalidDataException();
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, token);
        return new(first[0], first[1], headers, body);
    }

    static Task ReplyAsync(NetworkStream stream, int status, object body, CancellationToken token) =>
        SendAsync(stream, status, JsonSerializer.SerializeToUtf8Bytes(body), "application/json; charset=utf-8", token);

    static async Task SendAsync(NetworkStream stream, int status, byte[] body, string type, CancellationToken token)
    {
        string reason = status switch { 200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", _ => "Bad Gateway" };
        byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(body, token);
    }

    public ValueTask DisposeAsync() => new(disposing ??= CloseAsync());

    async Task CloseAsync()
    {
        stopping.Cancel();
        listener.Stop();
        await accepting;
        Task[] pending;
        lock (clients) pending = clients.ToArray();
        await Task.WhenAll(pending);
        // 完成回调在任务结束后仍可能释放capacity；不在此处Dispose该信号量。
        stopping.Dispose();
    }
}
