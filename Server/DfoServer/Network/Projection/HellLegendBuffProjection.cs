using System.Buffers.Binary;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class HellLegendBuffProjection
{
    // 01ECC36D对expire_time读取动态第0项的截止秒，模板第0项提供进度条总秒数。
    internal static byte[] AddedBody(long untilSeconds)
    {
        if (untilSeconds is <= 0 or > int.MaxValue) throw new InvalidDataException("星空药剂显示截止时间越界");
        byte[] body = new byte[17];
        body[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1), HellLegendEpicDropBuffDefinition.CanonicalBuffId);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(5), checked((int)untilSeconds));
        return body;
    }

    public static Task PostAsync(GameConnectionContext context, bool force = false, Func<bool>? canSend = null)
    {
        if (canSend?.Invoke() == false || context.Session.PlanHellLegendBuffProjection(force) is not { } receipt) return Task.CompletedTask;
        bool Current() => (canSend?.Invoke() ?? true) && receipt.IsCurrent();
        bool added = receipt.UntilSeconds > 0;
        var opcode = added ? NotiPacketType.CHARACTER_ADD_BUFF : NotiPacketType.CHARACTER_DEL_BUFF;
        var body = added ? AddedBody(receipt.UntilSeconds) :
            GuildEconomyPackets.RemovedBuffs([HellLegendEpicDropBuffDefinition.CanonicalBuffId]);
        bool queued = context.Sender.TryPost(0, (ushort)opcode, body,
            added ? "hell_legend_potion_buff_added" : "hell_legend_potion_buff_removed", Current, receipt.MarkSent);
        return queued ? Task.CompletedTask : Task.FromException(new IOException("Game send queue unavailable"));
    }
}
