using System.Buffers.Binary;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class DungeonEpicBuffProjection
{
    internal static byte[] AddedBody(int buff, int value = 1)
    {
        if (buff <= 0 || value < 0) throw new InvalidDataException("掉落Buff身份或动态参数无效");
        byte[] body = new byte[17]; body[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1), buff);
        // ARAD按PVF apply type解释：次数型传剩余次数，计时型传实际Unix截止秒。
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(5), value);
        return body;
    }

    public static Task PostAsync(GameConnectionContext context, bool force = false, Func<bool>? canSend = null)
    {
        if (canSend?.Invoke() == false || context.Session.PlanDungeonEpicBuffProjection(force) is not { } receipt) return Task.CompletedTask;
        bool Current() => (canSend?.Invoke() ?? true) && receipt.IsCurrent();
        for (int offset = 0; offset < receipt.RemovedIds.Length; offset += byte.MaxValue)
        {
            var removed = receipt.RemovedIds.Skip(offset).Take(byte.MaxValue).Select(id => checked((uint)id)).ToArray();
            bool last = offset + removed.Length == receipt.RemovedIds.Length && receipt.AddedBuffs.IsEmpty;
            if (!context.Sender.TryPost(0, (ushort)NotiPacketType.CHARACTER_DEL_BUFF,
                GuildEconomyPackets.RemovedBuffs(removed), "dungeon_epic_buff_removed", Current,
                last ? receipt.MarkSent : null)) return Task.FromException(new IOException("Game send queue unavailable"));
        }
        for (int i = 0; i < receipt.AddedBuffs.Length; i++)
        {
            var buff = receipt.AddedBuffs[i];
            if (!context.Sender.TryPost(0, (ushort)NotiPacketType.CHARACTER_ADD_BUFF,
                AddedBody(buff.BuffId, buff.Value), "dungeon_epic_buff_added", Current,
                i == receipt.AddedBuffs.Length - 1 ? receipt.MarkSent : null)) return Task.FromException(new IOException("Game send queue unavailable"));
        }
        return Task.CompletedTask;
    }
}
