using DfoServer.Game.Guild;

namespace DfoServer.Network.Builders;

internal static class GuildManagementPackets
{
    public static byte[] Error(ushort code) => [0, (byte)code, (byte)(code >> 8)];

    public static byte[] Invitation(GuildInvitation invite)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var state = invite.Guild;
        PacketEncoding.WriteString(writer, state.Guild.Name);
        PacketEncoding.WriteString(writer, invite.Inviter.Name);
        PacketEncoding.WriteString(writer, state.Promotion);
        writer.Write((uint)state.Guild.Level);
        writer.Write(state.Guild.MemberCount);
        byte[] tags = Enumerable.Range(0, 32).Where(bit => (state.Tags & (1u << bit)) != 0).Select(bit => (byte)bit).ToArray();
        writer.Write((byte)tags.Length);
        writer.Write(tags);
        writer.Write(state.Guild.Id);
        PacketEncoding.WriteString(writer, state.Guild.MasterName);
        writer.Write((uint)Math.Max(invite.Capacity, state.Members.Length));
        return stream.ToArray();
    }

    public static byte[] InvitationResult(byte result, string name)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(result);
        if (result < 2) PacketEncoding.WriteString(writer, name);
        return stream.ToArray();
    }

    public static byte[] Departed(GuildManagementCommit commit)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(commit.Voluntary ? (byte)1 : (byte)2);
        PacketEncoding.WriteString(writer, commit.GuildName);
        writer.Write((byte)0);
        PacketEncoding.WriteString(writer, commit.Removed?.Name ?? "");
        return stream.ToArray();
    }
}
