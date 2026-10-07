using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DfoServer.Network.Management;

internal static class DevelopmentGmClient
{
    public static bool TryExecute(int port, string line, out string response)
    {
        response = "";
        using var client = new TcpClient();
        using var connect = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        try
        {
            client.ConnectAsync(IPAddress.Loopback, port, connect.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }

        using var stream = client.GetStream();
        stream.ReadTimeout = 5000;
        stream.WriteTimeout = 5000;
        stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: false);
        response = reader.ReadLine() ?? throw new IOException("开发命令没有返回");
        return true;
    }
}
