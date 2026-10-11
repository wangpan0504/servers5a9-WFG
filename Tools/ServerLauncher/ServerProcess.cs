using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ServerLauncher;

internal sealed record LaunchSettings(string ServerIp, int ChannelPort, int GamePort, string PvfPath, string DataDirectory)
{
    public bool GmEnabled { get; init; }
    public int GmPort { get; init; } = 23102;
    public int GmWebPort { get; init; } = 23103;
    [System.Text.Json.Serialization.JsonIgnore] public int ActiveGmPort => GmEnabled ? GmPort : 0;

    public void Validate()
    {
        if (ServerIp != "auto" && (!IPAddress.TryParse(ServerIp, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork || address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast)))
            throw new InvalidDataException("服务地址请填写 IPv4 或 auto；本机使用 127.0.0.1。");
        int[] ports = GmEnabled ? [ChannelPort, GamePort, GmPort, GmWebPort] : [ChannelPort, GamePort];
        if (ports.Any(port => port is < 1 or > 65535) || ports.Distinct().Count() != ports.Length)
            throw new InvalidDataException("已启用的端口须在 1–65535 内，且不能重复。");
        if (string.IsNullOrWhiteSpace(PvfPath) || string.IsNullOrWhiteSpace(DataDirectory))
            throw new InvalidDataException("请选择 PVF 文件和存档目录。");
    }
}

internal sealed record ReadyInfo(int Pid, int ChannelPort, int GamePort, int GmPort, string? AdvertisedAddress,
    string? ListenAddress, string? PvfPath, string? DataDirectory);

internal sealed class ServerProcess(string root, Action<string> log) : IAsyncDisposable
{
    Process? process;
    Task? output, errors;
    bool ready;
    public bool HasExited => process is null || process.HasExited;
    public bool OwnsGmConnection(TcpClient client) => ready && !HasExited && process is { } owned &&
        LocalConnectionOwner.Matches(client, owned.Id);

    public async Task<ReadyInfo> StartAsync(LaunchSettings settings, CancellationToken token)
    {
        settings.Validate();
        string executable = Program.BundledServer
            ? Environment.ProcessPath ?? throw new IOException("无法确定本次单文件程序位置。")
            : Path.Combine(root, "DfoServer.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("请将启动器放在 DfoServer.exe 所在的完整服务包中。");
        string pvf = Path.GetFullPath(settings.PvfPath, root);
        string data = Path.GetFullPath(settings.DataDirectory, root);
        if (!File.Exists(pvf))
            throw new FileNotFoundException("PVF 文件不存在，请重新选择资源文件。");
        if (File.Exists(data))
            throw new InvalidDataException("存档位置需要选择目录，服务会使用其中的 game.db。");
        Directory.CreateDirectory(data);
        // 核对同一数据库的实例锁后才清理旧停止标记，不接管现有游戏进程。
        using (var guard = new FileStream(Path.Combine(data, "server.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None))
        {
            string priorStop = Path.Combine(root, "Logs", "stop.request");
            if (File.Exists(priorStop))
                File.Delete(priorStop);
        }
        IPAddress listen = settings.ServerIp == "auto" ? IPAddress.Any : IPAddress.Parse(settings.ServerIp);
        if (!IPAddress.IsLoopback(listen))
            listen = IPAddress.Any;
        foreach (var (address, port) in new[] { (listen, settings.ChannelPort), (listen, settings.GamePort) }
            .Concat(settings.GmEnabled ? new[] { (IPAddress.Loopback, settings.GmPort), (IPAddress.Loopback, settings.GmWebPort) } : []))
        {
            var check = new TcpListener(address, port);
            try { check.Start(); }
            catch (SocketException) { throw new IOException($"端口 {port} 已被占用或不可监听，请修改端口或正常停止旧服务。"); }
            finally { check.Stop(); }
        }
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (Program.BundledServer)
            start.ArgumentList.Add("--server");
        foreach (string argument in new[] { "--server-ip", settings.ServerIp, "--channel-port", settings.ChannelPort.ToString(),
            "--game-port", settings.GamePort.ToString(), "--gm-port", settings.ActiveGmPort.ToString() })
            start.ArgumentList.Add(argument);
        // 默认布局仍可使用已有服务包；自定义路径由新版服务端显式接收。
        if (!pvf.Equals(Path.Combine(root, "Data", "Pvf", "Script.pvf"), StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add("--pvf-path");
            start.ArgumentList.Add(pvf);
        }
        if (!data.Equals(Path.Combine(root, "Data"), StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add("--data-dir");
            start.ArgumentList.Add(data);
        }
        token.ThrowIfCancellationRequested();
        process = Process.Start(start) ?? throw new IOException("游戏服务未能启动。");
        output = Task.Run(() => ReadOutputAsync(process.StandardOutput));
        errors = Task.Run(() => ReadOutputAsync(process.StandardError));
        log("正在加载资源并启动服务…");
        log("PVF 资源：" + pvf);
        log("存档目录：" + data);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        string receipt = Path.Combine(root, "Logs", "ready.json");
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                await Task.WhenAll(output, errors);
                throw new IOException($"游戏服务启动失败（退出码 {process.ExitCode}），请查看下方输出及 Logs/server.jsonl。");
            }
            try
            {
                using var stream = new FileStream(receipt, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var state = await JsonSerializer.DeserializeAsync<ReadyInfo>(stream, cancellationToken: deadline.Token);
                if (state is not null && state.Pid == process.Id && state.ChannelPort == settings.ChannelPort &&
                    state.GamePort == settings.GamePort && state.GmPort == settings.ActiveGmPort)
                {
                    ready = true;
                    return state;
                }
            }
            catch (Exception error) when (error is IOException or JsonException) { }
            await Task.Delay(100, deadline.Token);
        }
    }

    async Task ReadOutputAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
            log(line.Length > 4096 ? line[..4096] + "…" : line);
    }

    public async ValueTask DisposeAsync()
    {
        if (process is null)
            return;
        bool wroteStop = false;
        string marker = Path.Combine(root, "Logs", "stop.request");
        try
        {
            if (!process.HasExited)
            {
                if (ready)
                {
                    await File.WriteAllTextAsync(marker, "stop");
                    wroteStop = true;
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
                    catch (TimeoutException)
                    {
                        log("正常停服等待超时，正在结束本启动器创建的服务进程。");
                        process.Kill();
                    }
                }
                else
                    process.Kill();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            log("正常停止未完成，正在结束本次创建的服务进程：" + error.Message);
        }
        finally
        {
            // 即使停止标记无法写入，也只回收持有的子进程，避免丢失进程所有权。
            if (!process.HasExited)
            {
                try { process.Kill(); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await process.WaitForExitAsync();
            if (output is not null && errors is not null)
                await Task.WhenAll(output, errors);
            process.Dispose();
            process = null;
            if (wroteStop && File.Exists(marker))
                File.Delete(marker);
        }
    }
}
