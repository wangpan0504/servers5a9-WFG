using System.Buffers.Binary;
using DfoServer.Network.Protocol;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Projection;

internal static class BossSelectionBuffProjection
{
    public static async Task PostAsync(GameConnectionContext context, bool force = false, Func<bool>? canSend = null)
    {
        if (canSend?.Invoke() == false) return;
        if (context.Session.PlanBossSelectionProjection(force) is not { } receipt) return;
        bool Current() => (canSend?.Invoke() ?? true) && receipt.IsCurrent();
        if (receipt.RemovedBuffId != 0)
        {
            await context.Notify(NotiPacketType.CHARACTER_DEL_BUFF, GuildEconomyPackets.RemovedBuffs([checked((uint)receipt.RemovedBuffId)]),
                "boss_selection_buff_removed", Current);
        }
        if (receipt.BuffId != 0)
            await context.Notify(NotiPacketType.CHARACTER_ADD_BUFF, AddedBody(receipt.BuffId, receipt.Remaining),
                "boss_selection_buff_added", Current);
    }

    // effect_apply=7；01ECCAC0读取参数向量第0项，以dstr30460显示剩余次数。
    // 487前两动态项先入向量，再追加模板参数；每日截止由服务端488显式移除。
    internal static byte[] AddedBody(int buffId, int remaining)
    {
        if (buffId is not (300 or 1275 or 1282) || remaining < 1 || remaining > (buffId == 1282 ? 1 : 5))
            throw new InvalidDataException("白斯BUFF投影无效");
        byte[] body = new byte[17];
        body[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1), buffId);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(5), remaining);
        return body;
    }
}
