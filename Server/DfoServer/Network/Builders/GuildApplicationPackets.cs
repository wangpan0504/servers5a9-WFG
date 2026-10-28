using DfoServer.Game.Guild;

namespace DfoServer.Network.Builders;

internal static class GuildApplicationPackets
{
    public static byte[] PendingApplication(GuildDirectoryEntry? guild)
    {
        // NOTI308的首字节是有无申请，不是CMD公共成功前缀。
        if (guild is null) return [0];
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(guild.Id);
        Text(writer, guild.Name);
        Text(writer, guild.MasterName);
        writer.Write(0u);
        writer.Write((uint)guild.Members);
        writer.Write((byte)0);
        Text(writer, guild.Promotion);
        return stream.ToArray();
    }

    public static byte[] Failure(ushort error, ReadOnlySpan<byte> identity = default)
    {
        byte[] body = new byte[3 + identity.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), error);
        identity.CopyTo(body.AsSpan(3));
        return body;
    }

    public static byte[] Search(GuildSearchPage page) => Build(writer =>
    {
        writer.Write(page.Total);
        writer.Write(page.Page);
        writer.Write(checked((byte)page.Guilds.Length));
        foreach (var guild in page.Guilds)
        {
            DirectoryFields(writer, guild);
            // 00598A1F、00598A39、00598A47：搜索行有独立的u32及两个u8尾字段。
            writer.Write(0u);
            writer.Write(guild.Tier);
            writer.Write(guild.Level);
        }
    });

    public static byte[] JoinCheck(GuildDirectoryEntry guild, int limit) => Build(writer =>
    {
        Text(writer, guild.Name);
        Text(writer, guild.MasterName);
        writer.Write((uint)guild.Members);
        writer.Write(checked((uint)limit));
        writer.Write(guild.JoinMode);
    });

    public static byte[] Applied(GuildDirectoryEntry guild) => Build(writer =>
    {
        writer.Write(guild.Id);
        Text(writer, guild.Name);
        Text(writer, guild.MasterName);
        writer.Write((uint)guild.Members);
        writer.Write(guild.JoinMode);
    });

    public static byte[] Applications(GuildApplicationRow[] rows) => Build(writer =>
    {
        writer.Write(checked((uint)rows.Length));
        foreach (var row in rows)
        {
            writer.Write((uint)row.CharacterId);
            Text(writer, row.Name);
            writer.Write(row.Job);
            writer.Write(row.GrowType);
            writer.Write(row.Level);
            writer.Write((byte)0);
            Text(writer, row.Message);
            writer.Write(row.CreatedAtUnix);
            writer.Write((byte)0);
            writer.Write(row.Region);
        }
    });

    public static byte[] AccountIndex(GuildAccountDirectory account) => Build(writer =>
    {
        writer.Write(checked((byte)account.Guilds.Length));
        foreach (var guild in account.Guilds)
        {
            DirectoryFields(writer, guild);
            // 005994CB、005994D9：账号目录没有搜索行的u32尾，不能共用完整行。
            writer.Write(guild.Tier);
            writer.Write(guild.Level);
        }
        writer.Write(checked((byte)account.Members.Length));
        foreach (var member in account.Members)
        {
            writer.Write(member.Guild!.Id);
            writer.Write(member.Region);
            Text(writer, member.Name);
        }
    });

    public static byte[] AccountApplications(GuildAccountDirectory account) => Build(writer =>
    {
        writer.Write(checked((byte)account.Applications.Length));
        foreach (var application in account.Applications)
        {
            Text(writer, application.Name);
            Text(writer, application.Guild!.Name);
            writer.Write((byte)1);
        }
    });

    public static byte[] Recommendations(GuildDirectoryEntry[] guilds) => Build(writer =>
    {
        writer.Write(checked((byte)guilds.Length));
        foreach (var guild in guilds)
        {
            writer.Write(guild.Id);
            Text(writer, guild.Name);
            Text(writer, guild.MasterName);
        }
    });

    public static byte[] SearchResult(GuildDirectoryEntry guild) => Build(writer =>
    {
        writer.Write(guild.Id);
        Text(writer, guild.Name);
        writer.Write(guild.Members);
        Text(writer, guild.MasterName);
        writer.Write(guild.AttendanceCount);
        byte[] tags = Enumerable.Range(0, 7).Where(tag => (guild.Tags & (1u << tag)) != 0).Select(tag => (byte)tag).ToArray();
        writer.Write(checked((byte)tags.Length));
        writer.Write(tags);
        Text(writer, guild.Promotion);
        writer.Write(guild.JoinMode);
        // 023681B5起继续消费22B；详情页需要标签、排名、创建时间、人数上限及真实成长字段。
        writer.Write(guild.Tags);
        writer.Write(guild.Ranking); // 正式月榜缺失时为0，不用预计排名冒充。
        writer.Write(guild.CreatedAtUnix);
        writer.Write(guild.MemberLimit);
        writer.Write(0u); // 02368221读取后未使用。
        writer.Write(guild.Tier);
        writer.Write(guild.Level);
    });

    static void DirectoryFields(BinaryWriter writer, GuildDirectoryEntry guild)
    {
        writer.Write(guild.Id);
        writer.Write(guild.JoinMode);
        writer.Write(guild.Tags);
        writer.Write(guild.Members);
        // 0059890D/005993B9读取u32徽章；02DAB190拆为两层图案，不能填1B公会等级。
        writer.Write(guild.Mark);
        Text(writer, guild.Name);
        Text(writer, guild.MasterName);
        Text(writer, guild.Promotion);
    }

    static void Text(BinaryWriter writer, string text) => PacketEncoding.WriteString(writer, text);
    static byte[] Build(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        write(writer);
        if (stream.Length > 65520) throw new InvalidDataException("Guild response exceeds frame limit");
        return stream.ToArray();
    }
}
