using DfoServer.Network.Builders;
using System.Buffers.Binary;
using DfoServer.Infrastructure;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Channels;

namespace DfoServer.Network.Protocol;

internal sealed class ChannelProtocolHandler(ServerOptions options, GameChannelListeners gameChannels, ChannelCatalog channels, ServerLog log) : IConnectionProtocol
{
    readonly ReadOnlyMemory<byte> scriptFrame = ScriptFrame(channels.SourceText);

    static byte[] ScriptFrame(string source)
    {
        byte[] bytes = CharacterName.Encoding.GetBytes(source);
        if (bytes.Length > 65520)
            throw new InvalidDataException("频道脚本编码后超过传输上限");
        byte[] frame = ChannelPackets.Script(bytes);
        if (frame.Length > 65536)
            throw new InvalidDataException("频道脚本压缩帧超过传输上限");
        return frame;
    }

    public async Task RunAsync(ConnectionContext connection)
    {
        bool hello = false, script = false;
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
                reply = scriptFrame.ToArray();
            }
            else if (b[1] == 17 && b.Length == 35 && hello)
            {
                reply = ChannelPackets.Directory(connection.ServerAddress(options), gameChannels.Endpoints);
            }
            else
                throw new InvalidDataException("Channel handshake order");
            await connection.WriteAsync(reply);
            log.Write("channel", new { Connection = connection.Id, Request = b[1], Reply = reply[1] });
            // 已缓存脚本的原生重连只发11/17；目录写完后同样结束握手，不等待不存在的9。
            if (b[1] == 17)
                return;
        }
    }

}
