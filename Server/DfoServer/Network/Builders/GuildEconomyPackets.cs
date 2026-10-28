using DfoServer.Game.Characters;
using DfoServer.Game.Guild;

namespace DfoServer.Network.Builders;

internal static class GuildEconomyPackets
{
    public static byte[] History(GuildMileageEntry[] rows)
    {
        if (rows.Length > 50) throw new InvalidDataException("公会积分历史超过显示预算");
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write((byte)1); writer.Write((uint)rows.Length);
        foreach (var row in rows)
        {
            writer.Write(checked((uint)row.UnixSeconds));
            int length = row.Name.Length;
            while (length > 0 && CharacterName.Encoding.GetByteCount(row.Name.AsSpan(0, length)) > 29) length--;
            byte[] name = CharacterName.Encoding.GetBytes(row.Name[..length]);
            writer.Write(name); writer.Write(new byte[30 - name.Length]);
            writer.Write(row.Kind); writer.Write((byte)0); writer.Write(row.ContentId); writer.Write(row.Amount); writer.Write(0u);
        }
        return stream.ToArray();
    }
    public static byte[] AddedBuffs(GuildBenefit[] benefits, long now)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(checked((byte)benefits.Length));
        foreach (var benefit in benefits)
        {
            writer.Write(benefit.Value); writer.Write(0u);
            writer.Write(benefit.ExpiresUnix == 0 ? 0u : checked((uint)Math.Max(0, benefit.ExpiresUnix - now)));
            writer.Write(0u);
        }
        return stream.ToArray();
    }
    public static byte[] RemovedBuffs(uint[] ids)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(checked((byte)ids.Length)); foreach (uint id in ids) writer.Write(id);
        return stream.ToArray();
    }
    public static byte[] Benefits(GuildEconomyState state)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(checked((byte)(state.Benefits.Length + 1)));
        foreach (var benefit in state.Benefits)
        {
            writer.Write(benefit.Type); writer.Write(checked((uint)benefit.ExpiresUnix));
            writer.Write(benefit.Type == 7 ? (byte)1 : (byte)0); writer.Write(benefit.Value);
        }
        writer.Write((byte)6); writer.Write(0u); writer.Write((byte)0); writer.Write((uint)state.MemberLimit);
        return stream.ToArray();
    }
}
