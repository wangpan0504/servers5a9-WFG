using System.Buffers.Binary;
using DfoServer.Game.Guild;
using DfoServer.Game.Town;

namespace DfoServer.Network.Builders;

internal static class GuildTeleportPackets
{
    public static byte[] Rejected(byte town, ushort error = 7) =>
        TeleportPackets.PartyRejected(new PartyTeleportRequest(new(town, 0, 0, 0, 5), 0), error: error);
    public static byte[] Status(GuildTeleportStatus notice)
    {
        byte[] body = new byte[30];
        body[0] = notice.Offer.Request.Target.Town; body[1] = notice.Phase;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(3), notice.Offer.Leader.Slot);
        for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(5 + i * 3), ushort.MaxValue);
        foreach (var (id, slot, answer) in notice.Answers)
        {
            if (slot > 7) throw new InvalidDataException("公会传送队伍槽越界");
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(5 + slot * 3), id); body[7 + slot * 3] = answer;
        }
        return body;
    }
}
