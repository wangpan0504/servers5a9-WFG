using DfoServer.Game.Guild;

namespace DfoServer.Network.Builders;

internal static class GuildPackets
{
    public static byte[] Invite(GuildPermit permit)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        PacketEncoding.WriteString(writer, permit.Name);
        writer.Write(permit.Region);
        PacketEncoding.WriteString(writer, permit.InitiatorName);
        PacketEncoding.WriteString(writer, permit.Promote);
        return stream.ToArray();
    }

    public static byte[] Created(uint guildId, string guildName)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(guildId);
        PacketEncoding.WriteString(writer, guildName);
        return stream.ToArray();
    }

    public static byte[] Entered(uint guildId, ushort characterId, string guildName, string characterName)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(guildId);
        writer.Write((uint)characterId);
        PacketEncoding.WriteString(writer, guildName);
        PacketEncoding.WriteString(writer, characterName);
        return stream.ToArray();
    }

    public static byte[] Information(GuildMembership guild, byte region)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        PacketEncoding.WriteString(writer, guild.Name);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write(guild.MemberCount);
        writer.Write((byte)0);
        PacketEncoding.WriteString(writer, guild.Notice);
        writer.Write(guild.Level);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write(0u); // 公会徽章，不把下一项经验错写进徽章字段。
        writer.Write(guild.Experience);
        PacketEncoding.WriteString(writer, guild.MasterName);
        PacketEncoding.WriteString(writer, guild.MasterName);
        writer.Write((ushort)0);
        PacketEncoding.WriteString(writer, "");
        writer.Write(new byte[168]); // 六条固定28B职位记录；权限管理尚未接入。
        writer.Write((byte)0); // 福利数量。
        writer.Write(0u);
        writer.Write((byte)0);
        writer.Write(0u);
        writer.Write((byte)0);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write((ushort)0);
        writer.Write((byte)0);
        writer.Write(region);
        writer.Write(0u);
        writer.Write((byte)0);
        writer.Write(guild.CreatedAtUnix);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(new byte[8]);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write((byte)0);
        return stream.ToArray();
    }
}
