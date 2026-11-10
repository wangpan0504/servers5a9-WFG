using System.Buffers.Binary;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class HellLegendBuffProjection
{
    // 01ECC36D对expire_time读取动态第0项的截止秒，模板第0项提供进度条总秒数。
    internal static byte[] AddedBody(long untilSeconds, int buffId = HellLegendEpicDropBuffDefinition.CanonicalBuffId)
    {
        if (untilSeconds is <= 0 or > int.MaxValue || buffId <= 0) throw new InvalidDataException("星空药剂显示截止时间越界");
        byte[] body = new byte[17];
        body[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1), buffId);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(5), checked((int)untilSeconds));
        return body;
    }

    public static Task PostAsync(GameConnectionContext context, bool force = false, Func<bool>? canSend = null)
    {
        if (canSend?.Invoke() == false || context.Session.PlanHellLegendBuffProjection(force) is not { } receipt) return Task.CompletedTask;
        bool Current() => (canSend?.Invoke() ?? true) && receipt.IsCurrent();
        bool added = receipt.UntilSeconds > 0;
        if (receipt.RemovedBuffId > 0 && !context.Sender.TryPost(0, (ushort)NotiPacketType.CHARACTER_DEL_BUFF,
            GuildEconomyPackets.RemovedBuffs([checked((uint)receipt.RemovedBuffId)]), "hell_legend_potion_buff_removed", Current,
            added ? null : receipt.MarkSent)) return Task.FromException(new IOException("Game send queue unavailable"));
        if (added && !context.Sender.TryPost(0, (ushort)NotiPacketType.CHARACTER_ADD_BUFF,
            AddedBody(receipt.UntilSeconds, receipt.BuffId), "hell_legend_potion_buff_added", Current, receipt.MarkSent))
            return Task.FromException(new IOException("Game send queue unavailable"));
        return Task.CompletedTask;
    }
}
