using DfoServer.Game.Dungeons;

namespace DfoServer.Network.Builders;

internal static class DazzlementTowerPackets
{
    // 006F8F20的type0只读取u8/i32/u8/i32/u8；offset6写+70，006F70F0据此返回日剩余0/1。
    // 层进度(+68)、通关标志(+6C)、领奖标志(+78)尚未实现，沿006F7D9A..006F7DA3初始化零值。
    // 只发送基础状态，避免type1/2/3清空或伪造尚未实现的贮藏箱、钥匙列表。
    public static byte[] State(byte usedToday)
    {
        if (usedToday > 1) throw new InvalidDataException("眩惑日准入已用次数越界");
        return [0, 0, 0, 0, 0, 0, usedToday, 0, 0, 0, 0];
    }

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
