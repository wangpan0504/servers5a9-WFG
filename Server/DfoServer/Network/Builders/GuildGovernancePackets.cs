using DfoServer.Game.Characters;
using DfoServer.Game.Guild;

namespace DfoServer.Network.Builders;

internal static class GuildGovernancePackets
{
    public static byte[] MemberGrade(byte region, string name, byte grade)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write((byte)0); // 原生126成功回调先消费一字节通知标记，再取角色身份及职位。
        writer.Write(region);
        PacketEncoding.WriteString(writer, name);
        writer.Write(grade);
        return stream.ToArray();
    }

    public static byte[] Roles(IReadOnlyList<GuildRole> roles)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        for (byte grade = 0; grade < 6; grade++)
        {
            var role = roles.Single(value => value.Grade == grade);
            writer.Write(role.Permissions);
            byte[] name = CharacterName.Encoding.GetBytes(role.Name);
            if (name.Length > 23) throw new InvalidDataException("公会职位名超过原生24字节缓冲");
            writer.Write(name);
            writer.Write(new byte[24 - name.Length]);
        }
        return stream.ToArray();
    }

    public static byte[] BoardPage(GuildBoardPage page)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(page.Page);
        writer.Write(page.TotalCount);
        writer.Write((byte)page.Posts.Count);
        foreach (var post in page.Posts)
        {
            writer.Write(post.PostId);
            writer.Write(post.AuthorCharacterId);
            writer.Write(post.Grade);
            writer.Write(post.CreatedAtUnix);
            PacketEncoding.WriteString(writer, post.AuthorName);
            PacketEncoding.WriteString(writer, post.Content);
        }
        return stream.ToArray();
    }

    public static byte[] BoardPostAck(uint postId)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(postId);
        return stream.ToArray();
    }
}
