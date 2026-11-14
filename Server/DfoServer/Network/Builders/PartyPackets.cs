using DfoServer.Game.Party;

namespace DfoServer.Network.Builders;

internal static class PartyPackets
{
    // 023A19C0读取旧队伍槽u8、原因u8；reason0使用原生强制退队提示。
    public static byte[] Kicked(byte slot) => slot < 4 ? [slot, 0] : throw new ArgumentOutOfRangeException(nameof(slot));

    // 023A2237读取一个u8；023A24B3..24B8写入UDP管理器的主机槽位。
    public static byte[] Host(byte slot) => slot < 4 ? [slot] : throw new ArgumentOutOfRangeException(nameof(slot));

    public static byte[] Endpoints(PartyEndpointRow[] rows) => SocialPackets.Build(w =>
    {
        w.Write(checked((byte)rows.Length));
        foreach (var row in rows)
        {
            var e = row.Endpoint;
            w.Write(row.Actor); w.Write(e.InnerAddress); w.Write(e.OuterAddress);
            w.Write((byte)(e.Port >> 8)); w.Write((byte)e.Port);
            w.Write(row.Account); w.Write(e.Nat); w.Write(e.Mtu); w.Write((byte)100);
        }
    });
    public static byte[] Realtime((ushort Actor, byte Slot)[] rows) => SocialPackets.Build(w =>
    {
        // 023CE4B0写Data+35C，025FD5A4将其用于血条及无战斗实体时的蓝条；城镇为100%。
        w.Write(checked((byte)rows.Length));
        foreach (var row in rows) { w.Write(row.Actor); w.Write((byte)100); w.Write((byte)0); w.Write(row.Slot); }
    });
    public static byte[] Peer(ushort actor, byte mode, uint peer, PartyInviteDisplay? invite = null) => SocialPackets.Build(w =>
    {
        w.Write(actor); w.Write(mode); w.Write(peer);
        // 023D1C03/0F/5A读取，03930F10以DSTR31181..31183显示邀请者三项资料。
        if (invite is { } display) { w.Write(display.Fatigue); w.Write(display.Stamina); w.Write(display.AntiEvil); w.Write(0u); }
    });
    public static byte[] Response(ushort actor, byte mode = 0) => SocialPackets.Build(w =>
    {
        w.Write((byte)1); w.Write(actor); w.Write(mode);
    });
    public static byte[] Failure(ushort error, byte mode = 0) => SocialPackets.Build(w =>
    {
        w.Write((byte)0); w.Write(error); w.Write(mode);
    });
    public static byte[] Information(ushort id, byte server, byte channel, (byte Slot, ushort Actor)[] members, byte leader, bool removed,
        PartySettings? settings = null) => SocialPackets.Build(w =>
    {
        // 023B9E70：本频道成员已由Actor安装，名册不附跨频道角色块。
        w.Write((ushort)1); w.Write((ushort)9999);
        InformationBlock(w, id, server, channel, members, leader, removed ? (byte)3 : (byte)0, settings);
    });

    public static byte[] Members(ushort id, byte server, byte channel, (byte Slot, ushort Actor)[] members, byte leader,
        PartySettings settings) => SocialPackets.Build(w =>
    {
        w.Write((ushort)1); w.Write((ushort)9999);
        InformationBlock(w, id, server, channel, members, leader, 2, settings);
    });

    public static byte[] Directory(byte server, byte channel, PartySnapshot[] rows, int total) => SocialPackets.Build(w =>
    {
        // 查询头重置旧行的排序索引（不删除队内对象），随后按包内次序安装至多三页。
        // 总数为0必须回0页；空结果却回1页会令023B9F2E反复触发纠正页码查询。
        w.Write(checked((ushort)rows.Length)); w.Write(checked((ushort)((total + 9) / 10)));
        foreach (var row in rows)
            InformationBlock(w, row.Id, server, channel, row.Members.Select(m => (m.Slot, m.Actor.Character.Id)).ToArray(),
                row.Leader, 0, row.Settings);
    });

    static void InformationBlock(BinaryWriter w, ushort id, byte server, byte channel,
        (byte Slot, ushort Actor)[] members, byte leader, byte action, PartySettings? settings)
    {
        w.Write(id); w.Write(server); w.Write(channel);
        w.Write((byte)0); w.Write(action); w.Write((byte)0);
        if (action == 3) return;
        var info = settings ?? PartySettings.Default;
        if (action == 0)
        {
            // action0完整info块；保留已确认宽度的未命名字段为零，不借用S4固定8槽结构。
            w.Write((byte)0); w.Write(info.TitleCode);
            if (info.TitleCode == 0) PacketEncoding.WriteString(w, info.Title);
            // 01B4BC0F/22/86回填创建窗口：容量在+4C，目标副本在+74，招募选项在+1E4。
            w.Write((byte)0); w.Write(info.Capacity); w.Write(0u); w.Write(info.Difficulty); w.Write(info.Argument); w.Write(info.Flag);
            w.Write(info.Kind); w.Write(info.KindArgument); w.Write(new byte[4]); w.Write(info.Dungeon);
            w.Write(new byte[2]); w.Write(info.Option ?? PartySettings.Default.Option!.Value);
        }
        // 023BA64E/023BB88C：action2只更新成员及扩展，不读取设置段。
        w.Write(checked((byte)members.Length));
        foreach (var member in members.OrderBy(m => m.Slot))
        {
            w.Write(member.Slot); w.Write(member.Actor);
            w.Write(new byte[5]); // 023BAF86/99/AB/C0/FFE，五个独立u8。
        }
        w.Write((byte)0); // 023BB0F7额外成员记录数量。
        w.Write((byte)0); w.Write(leader); w.Write(new byte[5]);
        // 023BB427/43F用末u8覆盖模型+82；芮兹0399E450读取对应类型，完整名册和成员增量都必须保留它。
        w.Write(info.Kind);
        w.Write((byte)0); // 023BB8A9扩展类型；0不带后续数据。
    }
}
