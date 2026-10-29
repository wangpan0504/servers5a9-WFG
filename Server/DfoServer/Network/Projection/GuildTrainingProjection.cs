using System.Buffers.Binary;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 重建Actor只回放本实例的剩余训练期限，由客户端原生限时buff负责到期。
internal static class GuildTrainingProjection
{
    public static async Task PostAsync(GameConnectionContext context, bool clear = false)
    {
        if (context.Session.PlanGuildTrainingProjection(clear) is not { } receipt) return;
        await context.Notify(NotiPacketType.CHARACTER_DEL_BUFF, GuildEconomyPackets.RemovedBuffs([1281]),
            "guild_training_removed", receipt.IsCurrent);
        if (receipt.RemainingSeconds > 0)
        {
            await context.Notify(NotiPacketType.GUILD_TRAINER_BUFF_INFO, receipt.States.ToArray(),
                "guild_training_active_selection", receipt.IsCurrent);
            await context.Notify(NotiPacketType.CHARACTER_ADD_BUFF, ActiveBuff(receipt.RemainingSeconds),
                "guild_training_added", receipt.IsCurrent);
        }
    }

    internal static byte[] ActiveBuff(int remainingSeconds)
    {
        if (remainingSeconds <= 0) throw new InvalidDataException("公会训练效果剩余期限无效");
        byte[] body = new byte[17];
        body[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1), 1281);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(9), remainingSeconds);
        return body;
    }

}
