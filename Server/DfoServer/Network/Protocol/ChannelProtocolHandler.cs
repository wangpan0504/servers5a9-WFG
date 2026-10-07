using DfoServer.Network.Builders;
using System.Buffers.Binary;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Protocol;

internal sealed class ChannelProtocolHandler(ServerOptions options, ServerLog log) : IConnectionProtocol
{
    public async Task RunAsync(ConnectionContext connection)
    {
        bool hello = false, script = false;
        byte[] scriptBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Resources", "channel.txt"));
        while (true)
        {
            byte[] b = await connection.ReadAsync();
            if (b[0] != 0 || BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(6)) != 0 || b[10] != 1)
                throw new InvalidDataException("Channel header");
            byte[] reply;
            if (b[1] == 11 && b.Length == 43 && !hello)
            {
                hello = true;
                reply = ChannelPackets.Hello();
            }
            else if (b[1] == 9 && b.Length == 11 && hello && !script)
            {
                script = true;
                reply = ChannelPackets.Script(scriptBytes);
            }
            else if (b[1] == 17 && b.Length == 35 && hello)
            {
                reply = ChannelPackets.Directory(options.AdvertisedAddress, options.GamePort);
            }
            else
                throw new InvalidDataException("Channel handshake order");
            await connection.WriteAsync(reply);
            log.Write("channel", new { Connection = connection.Id, Request = b[1], Reply = reply[1] });
            if (b[1] == 17 && script)
                return;
        }
    }
}
