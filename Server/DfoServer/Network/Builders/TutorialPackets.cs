using DfoServer.Game.Tutorials;

namespace DfoServer.Network.Builders;

internal static class TutorialPackets
{
    public static byte[] Flag(TutorialFlagResult result)
    {
        if (result.Error != 0) return [0, (byte)result.Error, (byte)(result.Error >> 8)];
        if (result.Rewards.Count > byte.MaxValue) throw new InvalidDataException("教程奖励应答条数越界");
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write((byte)1); writer.Write(checked((byte)result.Rewards.Count));
        foreach (var row in result.Rewards)
        {
            writer.Write(row.Slot); writer.Write(row.ItemId); writer.Write(row.Count);
        }
        return stream.ToArray();
    }
}
