using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 网吧特权buff。etc/variousbufflist.etc的1249史诗网吧特权为四维各+100、全技能等级+1，图标3121，
// [apply type] expire_time 0 0表示永久。客户端不会按Actor的网吧身份字节自行补buff，系统分类buff只能由
// 服务端以NOTI487创建，因此进入城镇、进入副本和返城重建Actor后都要按当前身份补发一次。
internal static class PcRoomBuffProjection
{
    public const uint BuffId = 1249;

    // NOTI487正文：count:u8 + {buffId:u32, dynamic0:u32, dynamic1:u32, dynamic2:u32}×count。
    // 1249是永久模板，三项动态整数全为0；编码只服务这一个buff，不引入公会福利的第二套状态模型。
    internal static byte[] AddedBody()
    {
        using var stream = new MemoryStream(17); using var writer = new BinaryWriter(stream);
        writer.Write((byte)1); writer.Write(BuffId); writer.Write(0u); writer.Write(0u); writer.Write(0u);
        return stream.ToArray();
    }

    // NOTI488正文：count:u8 + buffId:u32×count。
    internal static byte[] RemovedBody()
    {
        using var stream = new MemoryStream(5); using var writer = new BinaryWriter(stream);
        writer.Write((byte)1); writer.Write(BuffId);
        return stream.ToArray();
    }

    // 重建Actor后补发；身份未开启时不发送。
    public static Task PostAsync(GameConnectionContext context, Func<bool>? canSend = null) =>
        context.Session.PremiumPcRoom
            ? context.Notify(NotiPacketType.CHARACTER_ADD_BUFF, AddedBody(), "pc_room_buff", canSend)
            : Task.CompletedTask;

    // 在线切换身份时同步：开启补发NOTI487，关闭沿NOTI488移除，不留残留buff。
    public static Task SetAsync(GameConnectionContext context, bool enabled, Func<bool>? canSend = null) =>
        enabled
            ? PostAsync(context, canSend)
            : context.Notify(NotiPacketType.CHARACTER_DEL_BUFF, RemovedBody(), "pc_room_buff_removed", canSend);
}
