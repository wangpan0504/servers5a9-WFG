using DfoServer.Game.Pvp;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;
using System.Collections.Immutable;

namespace DfoServer.Network.Projection;

internal static class PvpArenaResultProjection
{
    // 布局按本版消费者；未还原的显示业务值采用明确记录的本服兼容口径，不参与发奖。
    internal static byte[] Body(PvpArenaResultDelivery delivery)
    {
        if (delivery.Rows.IsDefaultOrEmpty || delivery.Rows.Length > 8 ||
            delivery.Rows.All(row => row.Character != delivery.Receipt.CharacterId))
            throw new InvalidDataException("决斗结果缺少本人真实结算行");
        byte winner = delivery.Record.Participants.Where(row => row.Outcome == PvpArenaOutcome.Win)
            .OrderBy(row => row.Seat).Select(row => row.Seat).DefaultIfEmpty(byte.MaxValue).First();
        // 普通分支前三个u32只读入未消费；第四个写历史对手记录，本服使用已验证死亡产生的击杀数。
        var rows = delivery.Rows.Select(row => new PvpArenaResultRow(row.Character, row.Kills,
            checked((byte)row.Grade), 0, 0, 0, row.Kills)).ToImmutableArray();
        // FFFF仅跳过对应提示，FF仍会进入事件26；这里保留资料的兼容值，不声称关闭所有事件。
        var tail = new PvpArenaResultTail(0, ushort.MaxValue, 0, winner == byte.MaxValue ? (byte)1 : (byte)0,
            byte.MaxValue, 0, []);
        return PvpArenaResultPackets.Ordinary(winner, checked((uint)delivery.WinPoints),
            checked((byte)delivery.Receipt.Change.Grade), unchecked((uint)delivery.Receipt.Change.ExperienceDelta), rows, tail);
    }

    public static bool TryQueue(GameConnectionContext context, PvpArenaService arena, PvpArenaResultDelivery delivery)
    {
        bool Current() => arena.CanPublishResultDelivery(delivery);
        if (!Current()) return false;
        byte[] body = Body(delivery);
        return context.Sender.TryPost(0, (ushort)NotiPacketType.END_PVP, body, "pvp_arena_result", Current);
    }
}
