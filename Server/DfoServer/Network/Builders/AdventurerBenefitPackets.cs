using DfoServer.Game.Events;

namespace DfoServer.Network.Builders;

internal static class AdventurerBenefitPackets
{
    // NOTI1761由00498680固定读取16B；00498290区分1=回归、2=新手，00498160读取+12的绝对期限。
    // +4是首次提示已展示标记（读者弹窗后自行置1），不是领取成功。GM主动设身份不重弹首次引导。
    public static byte[] Returning(long until, ushort welcome = 0, ushort daily = 0)
    {
        if (until is < 0 or > uint.MaxValue) throw new InvalidDataException("回归福利期限超出原生协议范围");
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write(until == 0 ? 0u : 1u);
        writer.Write(until == 0 ? (ushort)0 : (ushort)1);
        if (welcome > 2 || daily > 2 || until == 0 && (welcome != 0 || daily != 0))
            throw new InvalidDataException("回归奖励状态无效");
        writer.Write(welcome); writer.Write(daily); writer.Write((ushort)0); writer.Write((uint)until);
        return bytes.ToArray();
    }

    public static byte[] ReturningMissions(ReturningAdventurerState state)
    {
        if (state.Missions.Length > 100 || state.Missions.Select(row => row.Id).Distinct().Count() != state.Missions.Length ||
            state.Missions.Any(row => row.Id is < 1 or > 100 || row.State > 2)) throw new InvalidDataException("回归任务状态无效");
        // 00498700固定读取100条12B槽；不能发送变长列表或追加计数字节。
        byte[] bytes = new byte[1200];
        using var stream = new MemoryStream(bytes); using var writer = new BinaryWriter(stream);
        foreach (var row in state.Missions)
        {
            writer.Write((uint)row.Id); writer.Write(row.Progress); writer.Write(row.State);
        }
        return bytes;
    }

    public static byte[] ReturningAck()
    {
        // 004983B0仅取12B后丢弃，不消费字段；实际成败由1761/1762及已提交背包差量表达。
        return new byte[12];
    }
}
