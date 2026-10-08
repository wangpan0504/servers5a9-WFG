using DfoServer.Game.Characters;

namespace DfoServer.Network.Builders;

internal static class FatiguePackets
{
    // 023C7600固定五个u16。offset6同时更新当前/上一剩余量；ACK4专有的SelectionAuxiliary不在此包中。
    public static byte[] Current(CharacterFatigueState state)
    {
        state.Validate();
        using var stream = new MemoryStream(10);
        using var writer = new BinaryWriter(stream);
        writer.Write(state.Used);
        writer.Write(state.Maximum);
        writer.Write(state.UsedMaximum);
        writer.Write(state.Remaining);
        writer.Write(state.ContextAuxiliary);
        return stream.ToArray();
    }
}
