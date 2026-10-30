using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Network.Builders;

internal static class PreyNormalSequencePackets
{
    public static byte[] Information(PreyNormalSequenceInfo info)
    {
        var body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body, PreyNormalCatalog.SequenceId);
        body[4] = info.Stage;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(5), info.ClearBits);
        // 01C21650通过03FAA140写+80并按此键取203分支；不是卢克42的零占位。
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9), info.Branch);
        return body;
    }
}
