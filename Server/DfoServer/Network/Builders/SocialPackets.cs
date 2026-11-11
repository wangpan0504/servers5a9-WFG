using DfoServer.Game.Social;

namespace DfoServer.Network.Builders;

internal static class SocialPackets
{
    internal static byte[] Build(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        write(writer); return stream.ToArray();
    }
    public static byte[] Ack(ushort error) => Build(w =>
    {
        w.Write(error == 0 ? (byte)1 : (byte)0);
        if (error != 0) w.Write(error);
    });
    public static byte[] CharacterFriends(SocialRow[] rows) => Build(w =>
    {
        w.Write(0u); w.Write((uint)rows.Length);
        foreach (var row in rows)
        {
            var p = row.Link.Person;
            w.Write(row.Server); w.Write(row.Channel); PacketEncoding.WriteString(w, p.Name);
            w.Write(p.Level); w.Write((uint)p.Job); w.Write(p.Grow); w.Write((byte)0);
            w.Write(0u); w.Write((byte)0); w.Write((byte)0);
        }
    });
    public static byte[] AccountFriends(SocialRow[] rows) => Build(w =>
    {
        w.Write(checked((byte)rows.Length));
        foreach (var row in rows)
        {
            var p = row.Link.Person;
            // 023B4E48写角色名到行+0C，023B4ECE写冒险团昵称到+54；00AEB590按+54覆盖同一好友。
            // 012B4CA5把行首u16用于大区名称查询，不是角色Actor；本服大区与频道服务器来自同一身份。
            w.Write((ushort)row.Server); w.Write(row.Server); PacketEncoding.WriteString(w, p.Name);
            w.Write(p.Level); w.Write(p.Job); w.Write(p.Grow); w.Write(row.Channel);
            PacketEncoding.WriteString(w, p.Adventure); PacketEncoding.WriteString(w, row.Link.Memo);
            // 023B4F39写行+CC；012B2045/2058要求频道非零且此标记为0才绘制在线角色。
            // 在线与否由channel表示，不能把Online写入客户端的不可用标记。
            w.Write(0u); w.Write((byte)0); w.Write(row.Link.Group); w.Write((byte)0);
        }
    });
    public static byte[] CharacterPresence(SocialRow row) => Build(w =>
    {
        w.Write(row.Online ? row.Channel : (ushort)0); w.Write(row.Server);
        PacketEncoding.WriteString(w, row.Link.Person.Name); w.Write(row.Online ? (byte)1 : (byte)0); w.Write((byte)0);
    });
    public static byte[] Requests(AccountFriendRequest[] rows) => Build(w =>
    {
        w.Write(checked((byte)rows.Length));
        foreach (var row in rows)
        {
            w.Write(row.Outgoing ? (byte)1 : (byte)0);
            PacketEncoding.WriteString(w, row.Person.Adventure); PacketEncoding.WriteString(w, row.Message);
            // 00AE97A0仅保留截止时间晚于当前时间的申请；创建时间会被点击入口立即过滤。
            // 当前申请不自动过期。为localtime32→mktime32的本地时间中间值预留一天，避免正时区溢出。
            w.Write(0x7FFEAE7Fu);
        }
    });
    public static byte[] AccountGroups(AccountFriendGroup[] groups) => Build(w =>
    {
        // 023D0800固定读取四行，没有数量头；老账号空名称回放本地默认组名和图标。
        if (groups.Length != 4) throw new InvalidDataException("冒险团分组必须为四组");
        foreach (var group in groups) { w.Write(group.Icon); PacketEncoding.WriteString(w, group.Name); }
    });
    public static byte[] AccountAck(string name, byte group, string memo, bool includeDetails, byte? mode = null) => Build(w =>
    {
        w.Write((byte)1);
        if (mode is { } value) w.Write(value);
        PacketEncoding.WriteString(w, name);
        if (includeDetails) { w.Write(group); PacketEncoding.WriteString(w, memo); }
    });
    public static byte[] Name(string name) => Build(w => PacketEncoding.WriteString(w, name));
    static void Block(BinaryWriter w, SocialLink row, byte? server)
    {
        PacketEncoding.WriteString(w, row.Person.Name);
        var date = row.Created.ToOffset(TimeSpan.FromHours(8));
        w.Write(checked((byte)(date.Year - 1900))); w.Write((byte)(date.Month - 1)); w.Write((byte)date.Day);
        if (server is { } value) { w.Write(value); w.Write((byte)0); w.Write((byte)0); }
        PacketEncoding.WriteString(w, row.Person.Adventure); PacketEncoding.WriteString(w, row.Memo);
    }
    public static byte[] Blacklist(SocialLink[] rows, byte server) => Build(w =>
    {
        w.Write((byte)1); w.Write(checked((byte)rows.Length));
        foreach (var row in rows) Block(w, row, server);
    });
    public static byte[] BlockAdded(SocialLink row) => Build(w => { w.Write((byte)1); Block(w, row, null); });
}
