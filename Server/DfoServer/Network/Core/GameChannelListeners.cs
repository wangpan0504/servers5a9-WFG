using System.Net;
using System.Net.Sockets;
using DfoServer.GameWorld.Channels;

namespace DfoServer.Network;

internal sealed class GameChannelListeners(ServerOptions options, ChannelCatalog catalog)
{
    internal sealed record BoundChannel(TcpListener Listener, ChannelEndpoint Endpoint);
    readonly List<BoundChannel> bound = [];
    public IReadOnlyList<BoundChannel> Bound => bound;
    public IReadOnlyList<ChannelEndpoint> Endpoints { get; private set; } = [];

    public void Start()
    {
        if (bound.Count != 0) throw new InvalidOperationException("分类入口已启动");
        int count = catalog.Channels.Count - 1;
        int first = options.ChannelEntryPortBase;
        int[] reserved = [options.ChannelPort, options.GamePort, options.NativePort, options.GmPort, options.PacketHttpPort];
        if (first != 0 && (first + count - 1 > 65535 ||
            reserved.Any(port => port != 0 && port >= first && port < first + count)))
            throw new InvalidDataException("分类入口端口范围越界或与主监听重复");
        var address = IPAddress.Parse(options.ListenAddress);
        try
        {
            Bind(ChannelCatalog.PrimaryChannelNumber, options.GamePort);
            int ordinal = 0;
            foreach (var channel in catalog.Channels.Where(row => row.Number != ChannelCatalog.PrimaryChannelNumber))
                Bind(channel.Number, first == 0 ? 0 : first + ordinal++);
            var byNumber = bound.ToDictionary(row => (int)row.Endpoint.Entry.Identity.Number, row => row.Endpoint);
            Endpoints = Array.AsReadOnly(catalog.Channels.Select(row => byNumber[row.Number]).ToArray());
        }
        catch
        {
            Stop();
            throw;
        }

        void Bind(int number, int port)
        {
            var entry = catalog.Entry(number);
            var listener = new TcpListener(address, port);
            try
            {
                listener.Start();
                int actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                if (port == 0 && reserved.Contains(actualPort))
                    throw new InvalidOperationException("自动分类入口占用了保留端口");
                bound.Add(new(listener, new(entry, actualPort)));
            }
            catch
            {
                listener.Stop();
                throw;
            }
        }
    }

    public void Stop()
    {
        foreach (var channel in bound) channel.Listener.Stop();
        bound.Clear();
        Endpoints = [];
    }
}
