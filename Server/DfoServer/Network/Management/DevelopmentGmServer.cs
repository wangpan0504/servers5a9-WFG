using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DfoServer.Network.Management;

internal static class DevelopmentGmServer
{
    public static Task Run(int port, Func<string, string> handle, CancellationToken stop)
    {
        if (port is < 1 or > 65535)
            throw new InvalidDataException("Invalid GM port");
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return Loop(listener, handle, stop);
    }

    public static (int Port, Task Loop) StartEphemeral(Func<string, string> handle, CancellationToken stop)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return (((IPEndPoint)listener.LocalEndpoint).Port, Loop(listener, handle, stop));
    }

    static async Task Loop(TcpListener listener, Func<string, string> handle, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stop);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException error) when (stop.IsCancellationRequested && error.SocketErrorCode == SocketError.OperationAborted)
                {
                    // Windows 取消待接入操作时可能返回 995，而非 OperationCanceledException。
                    break;
                }

                using (client)
                {
                    try
                    {
                        if (client.Client.RemoteEndPoint is not IPEndPoint peer || !IPAddress.IsLoopback(peer.Address))
                            continue;
                        using var stream = client.GetStream();
                        using var read = CancellationTokenSource.CreateLinkedTokenSource(stop);
                        read.CancelAfter(TimeSpan.FromSeconds(5));
                        string response = handle(await ReadLineAsync(stream, 512, read.Token));
                        using var write = CancellationTokenSource.CreateLinkedTokenSource(stop);
                        write.CancelAfter(TimeSpan.FromSeconds(5));
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(response + "\n"), write.Token);
                        await stream.FlushAsync(write.Token);
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidDataException)
                    { /* 单个开发连接失败不停止监听。 */
                    }
                }
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    static async Task<string> ReadLineAsync(NetworkStream stream, int maxBytes, CancellationToken stop)
    {
        var buffer = new byte[maxBytes + 1];
        int n = 0;
        while (n <= maxBytes)
        {
            if (await stream.ReadAsync(buffer.AsMemory(n, 1), stop) == 0)
                throw new IOException("开发命令连接已关闭");
            if (buffer[n] == '\n')
                return Encoding.UTF8.GetString(buffer, 0, n > 0 && buffer[n - 1] == '\r' ? n - 1 : n);
            n++;
        }

        throw new InvalidDataException("命令过长");
    }
}
