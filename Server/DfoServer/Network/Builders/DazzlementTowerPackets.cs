using DfoServer.Game.Dungeons;

namespace DfoServer.Network.Builders;

internal static class DazzlementTowerPackets
{
    // 006F96E0注册NOTI1608→006F8390；count:u8，逐项长度前缀名称及job:u8。
    // 006F7270→006F7080以当前角色名查该名单，存在时跳过绑定确认。
    public static byte[] Bindings(DazzlementTowerBindingNotice notice)
    {
        var members = notice.Bindings.Where(row => row.CharacterId is not null).ToArray();
        if (members.Length > DazzlementTowerState.CharacterLimit || members.Any(row => row.Name.Length == 0 || row.Job > 15))
            throw new InvalidDataException("眩惑绑定通知的角色名单非法");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(checked((byte)members.Length));
        foreach (var member in members)
        {
            PacketEncoding.WriteString(writer, member.Name);
            writer.Write(member.Job);
        }
        return stream.ToArray();
    }
}
