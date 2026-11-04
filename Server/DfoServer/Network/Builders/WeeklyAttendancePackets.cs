using System.Buffers.Binary;
using DfoServer.Game.Events;

namespace DfoServer.Network.Builders;

internal static class WeeklyAttendancePackets
{
    public static byte[] State(WeeklyAttendanceSnapshot state)
    {
        byte[] body = new byte[459];
        void Number(int offset, int value) => BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), checked((uint)value));
        Number(4, state.Total);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), state.BonusMask);
        state.SignedWeeks.CopyTo(body, 0x19c);
        Number(0x1b4, state.Clears);
        Number(0x1bc, state.Catchups);
        Number(0x1c0, state.SignedThisWeek ? 1 : 0);
        Number(0x1c4, state.Week);
        body[0x1c8] = state.CanSign ? (byte)1 : (byte)0;
        body[0x1c9] = state.CanCatchup ? (byte)1 : (byte)0;
        body[0x1ca] = state.CanBonus ? (byte)1 : (byte)0;
        return body;
    }
    public static byte[] Ack(WeeklyAttendanceCommit? accepted) => accepted is null ? [0, 1] :
        accepted.MailIds.IsEmpty ? [1, 1, 0, 0, 0] : [1, 0, 0, 0, 0];
}
